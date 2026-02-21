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
    FileParameters,
    FileRequest,
    PlainText,
    FileNameList,
    Payload,
    GetConnections,
    FileDelete
}

// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Connection))]
[JsonSerializable(typeof(HashSet<Connection>))]
[JsonSerializable(typeof(FileListEntry))]
[JsonSerializable(typeof(HashSet<FileListEntry>))]
public partial class SourceGenerationContext : JsonSerializerContext { }

public class Connection
{
    [JsonInclude]
    public string ConnectionAddress {get; set;}

    [JsonInclude]
    public int ConnectionPort { get; set; }

    [JsonInclude]
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

/*
    Takes in all the items in the Command enum and initalizes event delegates for them.
    This makes creating command delegates (CommandCall) easier for new commands, and vice versa for old commands.

    Connecting a handler to a CommandCall managed by the class example:
    
    CommandManager[Command.InvokeServer] += OnServerInvoke;

    public async Task OnServerInvoke(ulong size, NetworkStream stream) { ... }
*/
public class CommandManager
{
    public delegate Task CommandCall(ulong size, NetworkStream stream);

    private Dictionary<Command, CommandCall> _handlers = new();

    public CommandCall this[Command command]
    {
        get
        {
            if (_handlers.ContainsKey(command))
            {
                return _handlers[command];
            }
            else
            {
                throw new IndexOutOfRangeException($"Command {command} not found");
            }
        }
        set
        {
            _handlers[command] = value;
        }
    }

    public CommandManager()
    {
        CommandCall handler = (_, _) => Task.CompletedTask;

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
    public int ServerPort = 8080;
    public int ServerAddressPort = 8081;

    public const string HEADER_SEPARATOR = "::";
    public const string HEADER_SETTER = "=";
    public ulong MAX_HEADER_SIZE { get; } = 1024 * 1024; // 1mb
    public ulong MAX_MESSAGE_SIZE { get; } = 16 * 1024 * 1024; // 16mb

    public const string FILE_NAME_HEADER = "FILE_NAME";

    // Create a path for the files directory
    public readonly string DIRECTORY_OUTPUT = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Files");

    public HashSet<Connection> Connections = new();
    public HashSet<FileListEntry> FileEntries = new();

    public CommandManager CommandManager = new CommandManager();

    public NetworkUtility()
    {
        // Create the files directory if it doesn't exist
        if (!Directory.Exists(DIRECTORY_OUTPUT))
            Directory.CreateDirectory(DIRECTORY_OUTPUT);

        CommandManager[Command.PlainText] += OnPlainTextCalled;
        CommandManager[Command.FileNameList] += OnFileNameListUpdate;
    }

    /*
        Creates a payload that allows for seamless communication between the client and server (or peer).

        It's formatted as [command (byte)][payloadSize (ulong)][payload (byte[])].

        The command manager creates event delegates for each command in the Command enum.

        When the client or server receives a payload, it first reads the command byte and size of the payload
        Once the header data is returned, the main network loop finds the related command event and invokes it.

        Once the command is invoked, the payload size and client's network stream are provided to the event handlers.
        From there, a buffer is created with the size provided, which is used to read the data from the stream into.

        After that, the data can be decoded for that header, allowing the program to perform specific events for granular control
    */
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

    public virtual async Task HandleConnectedClient(TcpClient client)
    {
        // Get the client's stream and listen for incoming commands
        NetworkStream stream = client.GetStream();
        
        while (true)
        {
            // Wait for a message header that contains the command byte, and the payload size
            var (command, size) = await GetMessageHeader(stream);

            // Get the command event for the command passed
            CommandManager.CommandCall? commandDelegate = CommandManager[(Command)command];

            // If the command event exists, invoke it and send over the payload size and stream
            if (commandDelegate is not null)
            {
                await commandDelegate.Invoke(size, stream);
            }
        }
    }

    // BASE HANDLERS
    public async Task OnPlainTextCalled(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't sending more data than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        // Create a buffer that we'll use to read from the stream to
        byte[] textBuffer = new byte[(int)size];

        // Read to the text buffer
        await stream.ReadExactlyAsync(textBuffer);

        // Decode the text and log it to the console
        string text = Encoding.UTF8.GetString(textBuffer);
        Console.WriteLine(text);
    }

    public async Task OnFileNameListUpdate(ulong size, NetworkStream stream)
    {
        // Ensure the client isn't sending more data than we can handle
        if (size > MAX_MESSAGE_SIZE || size > int.MaxValue)
        {
            await SendMessage(stream, "Header size exceeds 1mb");
            return;
        }

        byte[] content = new byte[(int)size];
        await stream.ReadExactlyAsync(content);

        using (MemoryStream memStream = new MemoryStream(content))
        {
            HashSet<FileListEntry>? deserializedEntries = await JsonSerializer.DeserializeAsync(memStream, SourceGenerationContext.Default.HashSetFileListEntry);

            if (deserializedEntries is null)
                return;

            FileEntries = deserializedEntries;
        }
    }

    public string GetParameterValue(string parameterString, string parameterQuery)
    {
        // Split the parameter string by the header separator constant to get the individual parameters
        string[] parameters = parameterString.Split(HEADER_SEPARATOR);

        // Search through the parameters for a match to the parameter query
        foreach (string parameter in parameters)
        {
            if (parameter.Contains(parameterQuery))
            {
                // If there is a match, split it by the header setter and return the second substring
                return parameter.Split(HEADER_SETTER)[1].Trim();
            }
        }

        // If no matches were found, throw an exception
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
        
        // Send the payload command and size buffer first, then the files bytes seperately 
        await stream.WriteAsync(payloadBuffer);
        await file.CopyToAsync(stream);
    }

    public async Task SendMessage(NetworkStream stream, string message)
    {
        // Send the message over to the connected client
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] payload = CreatePayload(Command.PlainText, messageBytes);
        await stream.WriteAsync(payload);
    }

    public async Task<(byte, ulong)> GetMessageHeader(NetworkStream stream)
    {
        // Create a buffer for the command and read to it from the stream
        byte[] commandBuffer = new byte[1];
        await stream.ReadExactlyAsync(commandBuffer);

        // Create a buffer for the payload length command and read to it from the buffer
        byte[] payloadLengthBuffer = new byte[sizeof(ulong)];
        await stream.ReadExactlyAsync(payloadLengthBuffer);

        // Swap byte order if out system is using little-endian
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(payloadLengthBuffer);

        // Convert the payload buffer bytes into a ulong
        ulong payloadLength = BitConverter.ToUInt64(payloadLengthBuffer);

        return (commandBuffer[0], payloadLength);
    }

    public HashSet<FileListEntry> GetFileList()
    {
        HashSet<FileListEntry> set = new ();

        // Iterate through the file directory and create new entries from the data
        foreach (string filePath in Directory.EnumerateFiles(DIRECTORY_OUTPUT))
        {
            // Get the file info from the file path so we can read the size
            FileInfo info = new FileInfo(filePath);

            // Create a new entry from the file's name and size
            FileListEntry entry = new FileListEntry(Path.GetFileName(filePath), info.Length);

            // Add it to the set
            set.Add(entry);
        }

        return set;
    }

    public async Task SendFileListToConnections()
    {
        // Serialize the file list into JSON and create a byte buffer out of the serialized JSON
        string serializedNames = JsonSerializer.Serialize(GetFileList(), SourceGenerationContext.Default.HashSetFileListEntry);
        byte[] fileNameBytes = Encoding.UTF8.GetBytes(serializedNames);

        // Create a payload from the JSON buffer
        byte[] payload = CreatePayload(Command.FileNameList, fileNameBytes);

        // Iterate through the connected clients and send the payload to them
        foreach (Connection connection in Connections)
        {
            if (connection.ConnectionStream is not null)
                await connection.ConnectionStream.WriteAsync(payload);
        }
    }

    // Search for a valid IPv4 address
    public IPAddress GetLocalIPv4Address()
    {
        // Check if we're connected to the network
        if (!NetworkInterface.GetIsNetworkAvailable()) throw new Exception("No networks available");

        // Iterate through the network interfaces to search for our local IPv4 address
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Ensure we're searching through ethernet and wireless network interfaces
            if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
            
            // Ensure the adapter supports IPv4
            if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;

            // Get the adapter properties and ensure it's within the InterNetwork family and is not the loopback address
            var properties = adapter.GetIPProperties();
            foreach (var ip in properties.UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                {
                    // Return the valid IPv4 address
                    return ip.Address;
                }
            }
        }

        throw new Exception("No valid IPv4 addresses found.");
    }
}
