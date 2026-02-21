using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Network;

public class NetworkServer : NetworkUtility
{
    public NetworkServer()
    {
        // Hook in our command functions into the command manager
        CommandManager[Command.FileParameters] += OnFileParametersCalled;
        CommandManager[Command.FileRequest] += OnFileRequest;
        CommandManager[Command.GetConnections] += OnConnectionListRequest;
        CommandManager[Command.FileDelete] += OnFileDelete;
    }
    
    public async Task OnFileParametersCalled(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't sending more data than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        // Create a buffer for the file parameters and decode them
        byte[] parameterContent = new byte[(int)size];
        await stream.ReadExactlyAsync(parameterContent);

        string decodedParameters = Encoding.UTF8.GetString(parameterContent);
        string fileName;

        // Attempt to parse the file name out of the file parameters
        try
        {
            fileName = GetParameterValue(decodedParameters, FILE_NAME_HEADER);
        }
        catch (IOException e)
        {
            Console.WriteLine(e.Message);
            return;
        }

        // Once we have the file name, create the absolute file path using the directory output and file name
        string filePath = Path.GetFullPath(Path.Combine(DIRECTORY_OUTPUT, fileName));

        // Ensure that our file path lands within the server's file directory
        // We don't want to allow others to upload any files to where they shouldn't be.
        if (!filePath.StartsWith(Path.GetFullPath(DIRECTORY_OUTPUT) + Path.DirectorySeparatorChar))
        {
            await SendMessage(stream, "Invalid file name");
            return;
        }

        // Get the command byte
        byte[] commandBuffer = new byte[1];
        await stream.ReadExactlyAsync(commandBuffer);

        if ((Command)commandBuffer[0] != Command.Payload)
        {
            await SendMessage(stream, "Invalid command following file transfer command. Expected command \"Payload\" after command \"FileParameters\"");
            return;
        }

        // Get the payload size (ulong, 8 bytes)
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

        // Since C# constrains us to 2GB of total application memory, we need to stream the data from the network directly to the file
        // This allows us to have downloads over 2GB while not overloading the user's memory
        while (bytesTotal < payloadSize)
        {
            // If the difference between the downloaded bytes and payload size exceeds the buffer's length, clamp it
            // Otherwise, we use the difference
            int toRead = (int)Math.Min((ulong)payloadBuffer.Length, payloadSize - bytesTotal);

            // Read from the network
            await stream.ReadExactlyAsync(payloadBuffer.AsMemory(0, toRead));

            // Add the amount of bytes we read from the stream to the bytesTotal cursor
            await file.WriteAsync(payloadBuffer.AsMemory(0, toRead));

            bytesTotal += (ulong)toRead;

            Console.WriteLine($"WROTE {bytesTotal} BYTES OUT OF {payloadSize}");
        }

        // Close the file and tell the client we're done installing the file
        file.Close();

        await SendFileListToConnections();
    }

    public async Task OnFileRequest(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't attempting to send data larger than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        // Create a buffer for the file name bytes 
        // and read from the network stream to the buffer
        byte[] fileNameBytes = new byte[(int)size];
        await stream.ReadExactlyAsync(fileNameBytes);

        // Decode the file name
        string fileName = Encoding.UTF8.GetString(fileNameBytes);

        // Get a list of file names currently present in the server's file directory
        HashSet<FileListEntry> fileNames = GetFileList();


        foreach (var entry in fileNames)
        {
            // If the entry name matches what the client asked for, 
            // construct a file path and send it to the user
            if (entry.Name == fileName)
            {
                string filePath = Path.Combine(DIRECTORY_OUTPUT, fileName);

                using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    await SendFile(stream, file, fileName);
                    break;
                }
            }
        }
    }

    public async Task OnFileDelete(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't attempting to send data larger than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        // Create a buffer for the file name bytes 
        // and read from the network stream to the buffer
        byte[] fileNameBytes = new byte[(int)size];
        await stream.ReadExactlyAsync(fileNameBytes);

        // Decode the file name
        string fileName = Encoding.UTF8.GetString(fileNameBytes);

        // Get a list of file names currently present in the server's file directory
        HashSet<FileListEntry> fileNames = GetFileList();

        foreach (var entry in fileNames)
        {
            // If the entry name matches what the client asked for, 
            // construct a file path and send it to the user
            if (entry.Name == fileName)
            {
                // Once we have the file name, create the absolute file path using the directory output and file name
                string filePath = Path.GetFullPath(Path.Combine(DIRECTORY_OUTPUT, fileName));

                // Ensure that our file path lands within the server's file directory
                // We don't want to allow others to delete any files outside of the file directory
                if (!filePath.StartsWith(Path.GetFullPath(DIRECTORY_OUTPUT) + Path.DirectorySeparatorChar))
                {
                    await SendMessage(stream, "Invalid file name");
                    return;
                }

                File.Delete(filePath);
            }
        }

        await SendFileListToConnections();
    }

    public async Task OnConnectionListRequest(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't sending more data than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        // Create a buffer for the connections set, read from the stream to the buffer, and clear it
        // We don't necessarily need any parameter for this operation, but we still need to 
        // process any bytes that were possibly sent over
        byte[] connectionBytes = new byte[(int)size];
        await stream.ReadExactlyAsync(connectionBytes);
        Array.Clear(connectionBytes);

        // Serialize the connections
        string serializedConnections = JsonSerializer.Serialize(Connections);

        // Encode the JSON to bytes and create a payload for them
        byte[] fileConnectionBytes = Encoding.UTF8.GetBytes(serializedConnections);
        byte[] payload = CreatePayload(Command.GetConnections, fileConnectionBytes);

        // Write the payload to the client's stream
        await stream.WriteAsync(payload);
    }
}