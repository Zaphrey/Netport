using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Network;

public class NetworkServer : NetworkUtility
{
    public NetworkServer()
    {
        CommandManager[Command.FileParameters] += OnFileParametersCalled;
        CommandManager[Command.FileRequest] += OnFileRequest;
    }

    public async Task OnFileParametersCalled(ulong size, NetworkStream stream)
    {
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

        string filePath = Path.GetFullPath(Path.Combine(DIRECTORY_OUTPUT, fileName));

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

        while (bytesTotal < payloadSize)
        {
            int toRead = (int)Math.Min((ulong)payloadBuffer.Length, payloadSize - bytesTotal);
            await stream.ReadExactlyAsync(payloadBuffer.AsMemory(0, toRead));

            await file.WriteAsync(payloadBuffer.AsMemory(0, toRead));

            bytesTotal += (ulong)toRead;

            Console.WriteLine($"WROTE {bytesTotal} BYTES OUT OF {payloadSize}");
        }

        file.Close();

        await SendFileListToConnections();
    }

    public async Task OnFileRequest(ulong size, NetworkStream stream)
    {
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        byte[] fileNameBytes = new byte[(int)size];
        await stream.ReadExactlyAsync(fileNameBytes);
        string fileName = Encoding.UTF8.GetString(fileNameBytes);

        HashSet<FileListEntry> fileNames = GetFileList();

        foreach (var entry in fileNames)
        {
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

    public async Task OnConnectionListRequest(ulong size, NetworkStream stream)
    {
        byte[] buffer = new byte[size];
        await stream.ReadExactlyAsync(buffer);
        Array.Clear(buffer);

        HashSet<Connection> connections = Connections;
        string serializedConnections = JsonSerializer.Serialize(connections);
        byte[] fileConnectionBytes = Encoding.UTF8.GetBytes(serializedConnections.ToArray());
        byte[] payload = CreatePayload(Command.GetConnections, fileConnectionBytes);

        await stream.WriteAsync(payload);
    }
}