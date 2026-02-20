using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Network;

class Program
{
    private static NetworkServer _server = new NetworkServer();

    static async Task Main()
    {
        Environment.SetEnvironmentVariable("app_environment", "server");
        string hostName = Dns.GetHostName();

        IPHostEntry ipHostInfo = await Dns.GetHostEntryAsync(hostName);

        while (true)
        {
            try
            {
                await HandleIncomingConnections();
            }
            catch (Exception e)
            {
                // Server is down, clear all connections
                _server.Connections.Clear();
                Console.WriteLine(e.Message);
            }
        }
    }

    static async Task HandleIncomingConnections()
    {
        TcpListener listener = new(_server.GetLocalIPv4Address(), _server.ConnectionPort);

        listener.Start();

        while (true)
        {
            Console.WriteLine($"Server is listening at port: {_server.ConnectionPort}");

            TcpClient handler = await listener.AcceptTcpClientAsync();

            Thread connection = new Thread(CreateClientThread);

            connection.Start(handler);
        }
    }

    static async void CreateClientThread(object? client)
    {
        Connection? userConnection = null;

        try
        {
            if (client is not null && client is TcpClient)
            {
                NetworkStream stream = ((TcpClient)client).GetStream();

                // First message of the stream will always be information regarding the user's connection
                var (command, size) = await _server.GetMessageHeader(stream);

                if (size >= int.MaxValue || size >= _server.MAX_HEADER_SIZE)
                    return;

                byte[] content = new byte[size];
                await stream.ReadExactlyAsync(content);


                userConnection = JsonSerializer.Deserialize<Connection>(Encoding.UTF8.GetString(content));

                if (userConnection is not null)
                {
                    userConnection.ConnectionStream = stream;
                    _server.Connections.Add(userConnection);

                    Console.WriteLine(userConnection.ConnectionAddress);
                    Console.WriteLine(userConnection.ConnectionName);
                    Console.WriteLine(userConnection.ConnectionPort);

                    // userConnection.ConnectionPort = _server.ConnectionPort + _server.Connections.Count();
                    // byte[] payload = _server.CreatePayload(Command.SendAvailablePort, BitConverter.GetBytes(userConnection.ConnectionPort));
                    // await stream.WriteAsync(payload);
                }

                await _server.SendFileListToConnections();

                // New client connected, update other clients
                await UpdateClientConnections();

                await _server.HandleConnectedClient((TcpClient)client, isServer: true);
            }
        }
        catch (IOException e)
        {
            // User disconnected
            if (userConnection is not null)
            {
                _server.Connections.Remove(userConnection);

                // Client disconnected, update other clients
                await UpdateClientConnections();
            }

            Console.WriteLine("CAUGHT " + e.Message);
        }
    }

    static async Task UpdateClientConnections()
    {
        string json = JsonSerializer.Serialize(_server.Connections);
        byte[] payload = _server.CreatePayload(Command.GetConnections, Encoding.UTF8.GetBytes(json));
        
        foreach (Connection connection in _server.Connections)
        {
            if (connection.ConnectionStream == null)
                continue;

            await connection.ConnectionStream.WriteAsync(payload);
        }
    }
}