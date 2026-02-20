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
    
    public EventHandler<(string fileName, double progress)> DownloadProgressUpdate = delegate { };
    public EventHandler<string> DownloadStarted = delegate { };
    public EventHandler<(string fileName, bool completed)> DownloadStopped = delegate { };
    public EventHandler<Connection> ConnectionAdded = delegate { };
    public EventHandler<Connection> ConnectionRemoved = delegate { };
    
    public NetworkClient()
    {
        CommandManager[Command.FileParameters] += OnFileParametersCalled;
        CommandManager[Command.GetConnections] += OnGetConnections;
    }

    public async Task OnFileParametersCalled(ulong size, NetworkStream stream)
    {
        Console.WriteLine("FILE PARAMETERS CALLED");
        if (TopLevel is null)
            return;
        
        byte[] content = new byte[(int)size];
        await stream.ReadExactlyAsync(content);

        string decodedParameters = Encoding.UTF8.GetString(content);
        string? fileName = null;

        try
        {
            fileName = GetParameterValue(decodedParameters, FILE_NAME_HEADER);
        }
        catch (IOException e)
        {
            if (fileName is null)
                await SendMessage(stream, e.Message);

            return;
        }

        if (fileName is null)
        {
            await SendMessage(stream, "File name not provided in " + decodedParameters);
            return;
        }

        var saveFolder = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            return await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select directory to save file in",
                AllowMultiple = false,
            });
        });
        
        if (saveFolder.Count < 1) 
            return;

        string directoryPath = saveFolder[0].Path.LocalPath;
        Console.WriteLine(directoryPath);
        string filePath = Path.GetFullPath(Path.Combine(directoryPath, Path.GetFileName(fileName)));

        // Get the command byte
        byte[] commandBuffer = new byte[1];
        await stream.ReadExactlyAsync(commandBuffer);

        if ((Command)commandBuffer[0] != Command.Payload)
        {
            await SendMessage(stream, "Invalid command following file transfer command. Expected command \"Payload\" after command \"FileParameters\"");
            return;
        }

        // Get the payload size (ulong, 4 bytes)
        byte[] sizeBuffer = new byte[sizeof(ulong)];
        await stream.ReadExactlyAsync(sizeBuffer);

        if (!BitConverter.IsLittleEndian)
            Array.Reverse(sizeBuffer);

        ulong payloadSize = BitConverter.ToUInt64(sizeBuffer);

        int fileTransferBufferSize = 1024 * 512; // 512kb
        byte[] payloadBuffer = new byte[fileTransferBufferSize];
        ulong bytesTotal = 0;

        await using FileStream file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: fileTransferBufferSize, useAsync: true);
        DownloadStarted.Invoke(this, fileName);
        
        while (bytesTotal < payloadSize)
        {
            int toRead = (int)Math.Min((ulong)payloadBuffer.Length, payloadSize - bytesTotal);
            await stream.ReadExactlyAsync(payloadBuffer.AsMemory(0, toRead));

            await file.WriteAsync(payloadBuffer.AsMemory(0, toRead));

            bytesTotal += (ulong)toRead;

            DownloadProgressUpdate.Invoke(this, (fileName, (double)bytesTotal/payloadSize * 100));
        }

        file.Close();
        
        DownloadStopped.Invoke(this, (fileName, true));
    }

    public async Task OnGetConnections(ulong size, NetworkStream stream)
    {
        byte[] connectionBytes = new byte[size];
        await stream.ReadExactlyAsync(connectionBytes);

        HashSet<Connection> newConnections;

        using (MemoryStream memStream = new MemoryStream(connectionBytes))
        {
            HashSet<Connection>? deserializedEntries = await JsonSerializer.DeserializeAsync(memStream, SourceGenerationContext.Default.HashSetConnection);

            if (deserializedEntries is null)
                return;

            newConnections = deserializedEntries;
        }

        // Compare entries from the new list to see what's not inside the current list
        foreach (var newConnection in newConnections)
        {
            if (!Connections.Contains(newConnection) && newConnection.ConnectionPort != ConnectionPort)
            {
                ConnectionAdded?.Invoke(this, newConnection);
            }
        }
        
        // Compare entries in reverse order to see what entries in the current list aren't within the new list
        foreach (var oldConnection in Connections)
        {
            if (!newConnections.Contains(oldConnection) && oldConnection.ConnectionPort != ConnectionPort)
            {
                ConnectionRemoved?.Invoke(this, oldConnection);
            }
        }

        Connections = newConnections;
    }
}