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
        _ = Task.Run(HandleServerAddressRequests);

        await HandleIncomingConnections();
    }

    static async Task HandleIncomingConnections()
    {
        TcpListener listener = new(IPAddress.Any, _server.ConnectionPort);

        listener.Start();

        while (true)
        {
            Console.WriteLine($"Server is listening at port: {_server.ConnectionPort}");

            // Wait for client to connect and create a new thread that handles the client's commands to the server
            // This allows us to handle multiple clients at once
            TcpClient handler = await listener.AcceptTcpClientAsync();

            Thread connection = new Thread(CreateClientThread);

            connection.Start(handler);
        }
    }

    static async Task HandleServerAddressRequests()
    {
        // Create a UDP listener to listen for requests attempting to connect to the server
        UdpClient listener = new UdpClient();
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, _server.ServerAddressPort));

        while (true)
        {
            // Recieve and decode the message that was retrieved from the client
            var result = await listener.ReceiveAsync();
            string message = Encoding.UTF8.GetString(result.Buffer);

            // If the message matches what we've specified then send the server's local IP address back to the client
            if (message == "SERVER_ADDRESS")
            {
                // Encode the IP address
                byte[] response = Encoding.UTF8.GetBytes(_server.GetLocalIPv4Address().ToString());

                // Send a response back to the requester
                await listener.SendAsync(response, response.Length, result.RemoteEndPoint);
            }
        }
    }

    static async void CreateClientThread(object? client)
    {
        // Create a nullable connection. We'll receive the data for this from the client
        Connection? userConnection = null;

        try
        {
            if (client is null || client is not TcpClient)
            {
                throw new InvalidOperationException("Client received is null or is not a valid TcpClient");
            }

            // Get the client's network stream
            NetworkStream stream = ((TcpClient)client).GetStream();

            // First message of the stream will always be information regarding the user's connection
            var (command, size) = await _server.GetMessageHeader(stream);

            // If the client is attempting to send a large amount of data, 
            // deny the request and return early
            if (size >= int.MaxValue || size >= _server.MAX_HEADER_SIZE)
                return;

            // Create a buffer for the connection content
            byte[] connectionContent = new byte[size];

            // Write the connection content from the stream to the buffer
            await stream.ReadExactlyAsync(connectionContent);

            // Deserialize the connection content and assign it to the userConnection variable declared
            userConnection = JsonSerializer.Deserialize<Connection>(Encoding.UTF8.GetString(connectionContent));

            // Ensure the connection isn't null
            if (userConnection is not null)
            {
                // If not, then set the connection's stream to their network stream, 
                // and add it to the internal list of connections
                userConnection.ConnectionStream = stream;
                _server.Connections.Add(userConnection);

                // Once a new connection has been added, update all of the other connected clients
                await _server.SendFileListToConnections();
                await UpdateClientConnections();

                // Once we've taken care of our prerequisites, 
                // let the server utility handle the incoming commands from the client
                await _server.HandleConnectedClient((TcpClient)client);
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

            Console.WriteLine(e.Message);
        }
    }

    static async Task UpdateClientConnections()
    {
        // Serialize the list of client connections and send the payload to the connected clients
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