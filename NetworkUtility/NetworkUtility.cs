using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Network;

public enum Command : byte
{
    Header,
    Parameters,
    FileParameters,
    FileRequest,
    PlainText,
    FileNameList,
    Payload,
    GetConnections,
    SendAvailablePort,
}

public class Connection
{
    public string ConnectionAddress {get; set;}
    public int ConnectionPort { get; set; }
    public string ConnectionName { get; set; }

    [JsonIgnore]
    public NetworkStream? ConnectionStream { get; set; }

    public Connection(string connectionAddress, int connectionPort, string connectionName)
    {
        ConnectionAddress = connectionAddress;
        ConnectionPort = connectionPort;
        ConnectionName = connectionName;
    }
}

public record FileListEntry(string Name, long FileSize);

public class CommandManager
{
    public delegate Task CommandCall(ulong size, NetworkStream stream);

    private Dictionary<Command, CommandCall?> _handlers = new();

    public CommandCall? this[Command command]
    {
        get
        {
            if (_handlers.ContainsKey(command))
            {
                return _handlers[command];
            }
            else
            {
                return null;
            }
        }
        set
        {
            _handlers[command] = value;
        }
    }

    public CommandManager()
    {
        CommandCall? handler = null;

        // event CommandCall? commandCall;
        foreach (Command command in Enum.GetValues<Command>())
        {
            _handlers.Add(command, handler);
        }
    }
}

public class NetworkUtility
{
    // Port the server is listening on
    public int ConnectionPort { get; set; } = 8080;

    public const string HEADER_SEPARATOR = "::";
    public const string HEADER_SETTER = "=";
    public ulong MAX_HEADER_SIZE { get; } = 1024 * 1024; // 1mb
    public ulong MAX_MESSAGE_SIZE { get; } = 16 * 1024 * 1024; // 16mb

    public const string FILE_NAME_HEADER = "FILE_NAME";

    public readonly string DIRECTORY_OUTPUT = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Files");

    public string DeviceName { get; set; } = Dns.GetHostName();

    public HashSet<Connection> Connections = new();
    public HashSet<FileListEntry> FileEntries = new();

    public CommandManager CommandManager = new CommandManager();

    public NetworkUtility()
    {
        // Create the files directory if it doesn't exist
        if (!Directory.Exists(DIRECTORY_OUTPUT))
            Directory.CreateDirectory(DIRECTORY_OUTPUT);

        CommandManager[Command.Header] += OnHeaderCalled;
        CommandManager[Command.PlainText] += OnPlainTextCalled;
        CommandManager[Command.FileNameList] += OnFileNameListUpdate;
    }

    public byte[] CreatePayload(Command command, byte[] content)
    {
        // Command size, payload size, and payload length
        byte[] payload = new byte[sizeof(byte) + sizeof(ulong) + content.Length];

        // Set first byte of payload to command byte
        payload[0] = (byte)command;

        // Copy the content length bytes to the payload after the command byte
        byte[] lengthBytes = BitConverter.GetBytes((ulong)content.Length);
        Buffer.BlockCopy(lengthBytes, 0, payload, sizeof(byte), sizeof(ulong));

        // Copy the payload content to the payload after
        Buffer.BlockCopy(content, 0, payload, sizeof(byte) + sizeof(ulong), content.Length);

        return payload;
    }

    public virtual async Task HandleConnectedClient(TcpClient client, bool isServer = false)
    {
        NetworkStream stream = client.GetStream();

        await SendMessage(stream, $"Hello from {DeviceName}!");
        
        while (true)
        {
            var (command, size) = await GetMessageHeader(stream);
            Console.WriteLine(((Command)command).ToString());

            CommandManager.CommandCall? commandDelegate = CommandManager[(Command)command];

            if (commandDelegate is not null)
            {
                await commandDelegate.Invoke(size, stream);
            }
        }
    }

    // EVENT HANDLERS

    public async Task OnHeaderCalled(ulong size, NetworkStream stream)
    {
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        byte[] content = new byte[(int)size];
        await stream.ReadExactlyAsync(content);

        string decodedContent = Encoding.UTF8.GetString(content);
        string[] segments = decodedContent.Split(HEADER_SEPARATOR);

        // foreach (string header in segments)
        // {
        //     // Console.WriteLine(header);
        // }
    }

    public async Task OnPlainTextCalled(ulong size, NetworkStream stream)
    {
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        byte[] content = new byte[(int)size];
        await stream.ReadExactlyAsync(content);

        // Console.WriteLine(Encoding.UTF8.GetString(content));
    }

    public async Task OnFileNameListUpdate(ulong size, NetworkStream stream)
    {
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        byte[] content = new byte[(int)size];
        await stream.ReadExactlyAsync(content);
        
        Console.WriteLine(Encoding.UTF8.GetString(content));

        FileEntries = await DecodeHashSet<FileListEntry>(content);

        foreach (FileListEntry entry in FileEntries)
        {
            Console.WriteLine($"{entry.Name} {entry.FileSize}");
        }
    }

    public string GetParameterValue(string parameterString, string parameterQuery)
    {
        string[] parameters = parameterString.Split(HEADER_SEPARATOR);

        foreach (string parameter in parameters)
        {
            // Console.WriteLine(parameter);
            if (parameter.Contains(parameterQuery))
            {
                return parameter.Split(HEADER_SETTER)[1].Trim();
            }
        }

        throw new IOException($"Value \"{parameterQuery}\" was not found in parameter list: {parameterString}");
    }

    public async Task SendFile(NetworkStream stream, Stream file, string fileName)
    {
        // Send the file name over to the connected device
        byte[] fileNameBytes = Encoding.UTF8.GetBytes((FILE_NAME_HEADER + HEADER_SETTER + fileName).ToArray());
        byte[] payload = CreatePayload(Command.FileParameters, fileNameBytes);
        await stream.WriteAsync(payload);
        
        // Command size, payload size, and payload length
        byte[] payloadBuffer = new byte[sizeof(byte) + sizeof(ulong)];

        // Set first byte of payloadBuffer to command byte
        payloadBuffer[0] = (byte)Command.Payload;

        // Copy the content length bytes to the payload after the command byte
        byte[] lengthBytes = BitConverter.GetBytes((ulong)file.Length);
        Buffer.BlockCopy(lengthBytes, 0, payloadBuffer, sizeof(byte), sizeof(ulong));
        
        await stream.WriteAsync(payloadBuffer);
        await file.CopyToAsync(stream, 1024 * 512);
    }

    public async Task SendMessage(NetworkStream stream, string message)
    {
        // Send the message over to the connected device
        byte[] messageBytes = Encoding.UTF8.GetBytes(message.ToArray());
        byte[] payload = CreatePayload(Command.PlainText, messageBytes);
        await stream.WriteAsync(payload);
    }

    public async Task<(byte, ulong)> GetMessageHeader(NetworkStream stream)
    {
        // byte[] commandLength = new byte[sizeof(int)];
        int command = stream.ReadByte();

        if (command == -1)
        {
            throw new IOException("Client disconnected");
        }

        byte[] payloadLengthBuffer = new byte[sizeof(ulong)];

        await stream.ReadExactlyAsync(payloadLengthBuffer);

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payloadLengthBuffer);
        }

        ulong payloadLength = BitConverter.ToUInt64(payloadLengthBuffer);

        return ((byte)command, payloadLength);
        // while (headerLength.Length < )
    }

    public HashSet<FileListEntry> GetFileList()
    {
        HashSet<FileListEntry> set = new ();

        foreach (string filePath in Directory.EnumerateFiles(DIRECTORY_OUTPUT))
        {
            FileInfo info = new FileInfo(filePath);

            FileListEntry entry = new FileListEntry(Path.GetFileName(filePath), info.Length);

            set.Add(entry);
        }

        return set;
    }

    public async Task SendFileListToConnections()
    {
        string serializedNames = JsonSerializer.Serialize(GetFileList());
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(serializedNames.ToArray());
        byte[] payload = CreatePayload(Command.FileNameList, fileNameBytes);

        foreach (Connection connection in Connections)
        {
            Console.WriteLine($"{connection.ConnectionName} {connection.ConnectionAddress}:{connection.ConnectionPort}");
            if (connection.ConnectionStream is not null)
                await connection.ConnectionStream.WriteAsync(payload);
        }
    }

    public async Task<HashSet<T>> DecodeHashSet<T>(byte[] setData)
    {
        // string serializedNames = Encoding.UTF8.GetString(fileListData);
        
        using (MemoryStream stream = new MemoryStream(setData))
        {
            HashSet<T>? data = await JsonSerializer.DeserializeAsync<HashSet<T>>(stream);

            return data is not null ? data : new HashSet<T>();
        }
    }

    // Search for a valid IPv4 address
    public IPAddress GetLocalIPv4Address()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) throw new Exception("No networks available");

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;

            var properties = adapter.GetIPProperties();
            foreach (var ip in properties.UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                {
                    return ip.Address;
                }
            }
        }

        throw new Exception("No valid IPv4 addresses found.");
    }
}
