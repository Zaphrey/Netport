using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Network;

namespace client;

public class NetworkClient : NetworkUtility
{
    public TopLevel? TopLevel { get; set; }
    
    public event EventHandler<(string fileName, double progress)> DownloadProgressUpdate = delegate { };
    public event EventHandler<string> DownloadStarted = delegate { };
    public event EventHandler<(string fileName, bool completed)> DownloadStopped = delegate { };
    public event EventHandler<Connection> ConnectionAdded = delegate { };
    public event EventHandler<Connection> ConnectionRemoved = delegate { };
    
    public NetworkClient()
    {
        CommandManager[Command.FileParameters] += OnFileParametersCalled;
        CommandManager[Command.GetConnections] += OnGetConnections;
    }

    // Function that handles file downloads on the client
    public async Task OnFileParametersCalled(ulong size, NetworkStream stream)
    {
        // We need the "TopLevel" of the application to exist in order to gain access to the systems file explorer
        if (TopLevel is null)
            return;

        // Create a buffer for the file parameters and fill it with data from the stream
        byte[] fileParameters = new byte[(int)size];
        await stream.ReadExactlyAsync(fileParameters);

        // Decode the file parameters
        string decodedParameters = Encoding.UTF8.GetString(fileParameters);
        string fileName;

        // If we aren't able to extract the file name from the parameters, 
        // return early after logging the error
        try
        {
            fileName = GetParameterValue(decodedParameters, FILE_NAME_HEADER);
        }
        catch (IOException e)
        {
            Console.WriteLine(e.Message);

            return;
        }

        // Invoke Avalonia's UIThread and request the desired file directory from the storage provider
        var saveFolder = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            return await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select directory to save file in",
                AllowMultiple = false,
            });
        });
        
        // Check if the user selected a file
        if (saveFolder.Count < 1) 
            return;

        // Once we have both the desired directory and file name, combine them together to get the full path
        string directoryPath = saveFolder[0].Path.LocalPath;
        string filePath = Path.GetFullPath(Path.Combine(directoryPath, Path.GetFileName(fileName)));


        // Get the command byte and validate it
        byte[] commandBuffer = new byte[1];
        await stream.ReadExactlyAsync(commandBuffer);

        if ((Command)commandBuffer[0] != Command.Payload)
        {
            Console.WriteLine("Invalid command following file transfer command. Expected command \"Payload\" after command \"FileParameters\"");
            return;
        }

        // Get the payload size (ulong, 4 bytes)
        byte[] sizeBuffer = new byte[sizeof(ulong)];
        await stream.ReadExactlyAsync(sizeBuffer);

        // Swap byte order if out system is using little-endian
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(sizeBuffer);

        // Finally, get the file payload size
        ulong payloadSize = BitConverter.ToUInt64(sizeBuffer);

        // Once we've gotten all the information we needed, we can start writing the file from the stream
        // Create a buffer with 512kb of space. This is what we're using to transfer data from the network stream to the file stream
        int fileTransferBufferSize = 1024 * 512; // 512kb
        byte[] payloadBuffer = new byte[fileTransferBufferSize];

        // Byte cursor which helps us know how far we are along the download
        ulong bytesTotal = 0;

        // Create a file stream and specify the buffer size we created earlier
        await using FileStream file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: fileTransferBufferSize, useAsync: true);
        
        // Tell the client's UI that we've started a download
        DownloadStarted.Invoke(this, fileName);
        
        // Since C# constrains us to 2GB of total application memory, we need to stream the data from the network directly to the file
        // This allows us to have downloads over 2GB while not overloading the user's memory
        while (bytesTotal < payloadSize)
        {
            // If the difference between the downloaded bytes and payload size exceeds the buffer's length, clamp it
            // Otherwise, we use the difference
            int toRead = (int)Math.Min((ulong)payloadBuffer.Length, payloadSize - bytesTotal);

            // Read from the network
            await stream.ReadExactlyAsync(payloadBuffer, 0, toRead);

            // Write to the file
            await file.WriteAsync(payloadBuffer, 0, toRead);

            // Add the amount of bytes we read from the stream to the bytesTotal cursor
            bytesTotal += (ulong)toRead;

            // Update the client on how much progress has been made %
            DownloadProgressUpdate.Invoke(this, (fileName, (double)bytesTotal / payloadSize * 100));
        }

        // Close the file and tell the client we're done installing the file
        file.Close();
        
        DownloadStopped.Invoke(this, (fileName, true));
    }

    public async Task OnGetConnections(ulong size, NetworkStream stream)
    {
        // Get the connection set payload size and read it from the network to the bugger
        byte[] connectionBytes = new byte[size];
        await stream.ReadExactlyAsync(connectionBytes);

        // Initialize the newConnections set
        HashSet<Connection> newConnections;

        using (MemoryStream memStream = new MemoryStream(connectionBytes))
        {
            // Desetialize the connectionData asynchronously. The stream avoids string allocation according to this source
            // https://marcroussy.com/2020/08/17/deserialization-with-system-text-json/
            HashSet<Connection>? deserializedEntries = await JsonSerializer.DeserializeAsync(memStream, SourceGenerationContext.Default.HashSetConnection);

            // Return early if the data could not be deserialized
            if (deserializedEntries is null)
                return;

            // Assign the new connections set to the deserialized entry set
            newConnections = deserializedEntries;
        }

        // Compare entries from the new list to see what's not inside the current list
        foreach (var newConnection in newConnections)
        {
            if (!Connections.Contains(newConnection) && newConnection.ConnectionPort != ConnectionPort)
            {
                // Tell the client a new connection was added
                ConnectionAdded?.Invoke(this, newConnection);
            }
        }
        
        // Compare entries in reverse order to see what entries in the current list aren't within the new list
        foreach (var oldConnection in Connections)
        {
            if (!newConnections.Contains(oldConnection) && oldConnection.ConnectionPort != ConnectionPort)
            {
                // Tell the client that a previous connection was removed
                ConnectionRemoved?.Invoke(this, oldConnection);
            }
        }

        // Lastly, update the Connections variable to use the new set.
        Connections = newConnections;
    }
}