using Avalonia.Controls;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Network;

namespace client;

public partial class MainWindow : Window
{
    private TcpClient? _client;
    private NetworkClient _clientUtility = new NetworkClient();
    private TcpListener _listener;
    
    public MainWindow()
    {   
        InitializeComponent();

        _clientUtility.TopLevel = GetTopLevel(this);

        // Hook up command handlers to the command manager
        _clientUtility.CommandManager[Command.FileNameList] += (_, _) =>
        {
            Dispatcher.UIThread.Post(PopulateFileEntries);
            return Task.CompletedTask;
        };
        
        _clientUtility.DownloadStarted += (_, s) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AddFileToDownloads(s);
            });
        };
        
        _clientUtility.ConnectionAdded += async (sender, connection) =>
        {
            await OnConnectionAdded(connection);
        };
        
        _clientUtility.ConnectionRemoved += (sender, connection) =>
        {
            OnConnectionRemoved(connection);
        };
        
        // Start listening for peer 2 peer requests
        _listener = new(_clientUtility.GetLocalIPv4Address(), 0);
        _listener.Start();
        
        _clientUtility.ConnectionPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        
        Task.Run(InitServerConnection);
        Task.Run(HandleConnectingClients);
    }

    private void AddFileToDownloads(string fileName)
    {   
        // Search for where we're storing the downloaded elements
        var downloads = this.FindControl<StackPanel>("DownloadPanel");

        if (downloads is null)
            return;
        
        // Create file entry controls to render
        StackPanel panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        TextBlock nameBlock = new TextBlock
        {
            Text = fileName,
            Margin = new Thickness(2),
        };

        TextBlock downloadProgress = new TextBlock
        {
            Text = "0%",
            MinWidth = 60,
            Margin = new Thickness(2),
        };

        // Local function for updating the downloads progress
        void UpdateDownloadProgress(object? sender, (string fileName, double progress) args)
        {
            // Ensure we're not updating the wrong file
            if (args.fileName != fileName)
                return;
            
            // Dispatch it to Avalonia's UIThread to update the progress
            Dispatcher.UIThread.Post(() =>
            {
                downloadProgress.Text = $"{Math.Round(args.progress, 2)}%";
            });
        }

        void DownloadStopped(object? sender, (string fileName, bool completed) args)
        {
            // Ensure we're not updating the wrong file
            if (args.fileName != fileName)
                return;

            // Dispatch it to Avalonia's UIThread to update the completed progress
            Dispatcher.UIThread.Post(() =>
            {
                downloadProgress.Text = $"{ (args.completed ? "Completed" : "Failed") }";
            });

            // Disconnect the local functions from the download events
            _clientUtility.DownloadProgressUpdate -= UpdateDownloadProgress;
            _clientUtility.DownloadStopped -= DownloadStopped;
        }

        // Hook up the local functions to the download events
        _clientUtility.DownloadProgressUpdate += UpdateDownloadProgress;
        _clientUtility.DownloadStopped += DownloadStopped;
        
        // Add the controls to the main panel
        panel.Children.Add(nameBlock);
        panel.Children.Add(downloadProgress);

        // And parent the main panel to the downloads control
        downloads.Children.Add(panel);
    }
    
    private void PopulateFileEntries()
    {
        // Where we're storing the list of files in the interface
        StackPanel? fileLabels = this.FindControl<StackPanel>("FileLabels");

        // Id it doesn't exist, return early
        if (fileLabels is null)
            return;

        // Clear previous list
        fileLabels.Children.Clear();
            
        foreach (FileListEntry entry in _clientUtility.FileEntries)
        {
            // Create a new border, which will host the download and delete buttons along with file information
            Border border = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = Brushes.AliceBlue,
                Padding = new Thickness(0, 5, 0, 0),
            };
            
            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            Button downloadButton = new Button()
            {
                Content = "Download",
            };
            
            Button deleteButton = new Button()
            {
                Content = "Delete",
            };

            TextBlock fileText = new TextBlock()
            {
                Text = entry.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            // Find a suffix appropriate for the file's size
            // https://stackoverflow.com/a/2082893
            string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
            string suffix = suffixes[0];
            
            decimal sizeCopy = entry.FileSize;
            
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (sizeCopy < 1024)
                    break;
                
                sizeCopy /= 1024;
                suffix = suffixes[i + 1];
            }
            
            TextBlock sizeBlock = new TextBlock()
            {
                Text = Math.Round(sizeCopy, 2) + suffix,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Add a click handler for the download button
            downloadButton.Click += async (_, _) =>
            {
                // Get the bytes of the desired file name
                byte[] fileNameBytes = Encoding.UTF8.GetBytes(entry.Name);

                // Create a payload for the file request
                byte[] payload = _clientUtility.CreatePayload(Command.FileRequest, fileNameBytes);

                // Get the user's stream
                NetworkStream? stream = _client?.GetStream();

                if (stream is not null)
                    // Write the payload to the user's stream
                    await stream.WriteAsync(payload);
            };

            // Add a click handler for the delete button
            deleteButton.Click += async (_, _) =>
            {
                // Get the bytes of the desired file name
                byte[] fileNameBytes = Encoding.UTF8.GetBytes(entry.Name);

                // Create a payload for the file deletion request
                byte[] payload = _clientUtility.CreatePayload(Command.FileDelete, fileNameBytes);

                // Get the user's stream
                NetworkStream? stream = _client?.GetStream();

                if (stream is not null)
                    // Write the payload to the user's stream
                    await stream.WriteAsync(payload);
            };

            // Parent the elements to the panel
            panel.Children.Add(downloadButton);
            panel.Children.Add(deleteButton);
            panel.Children.Add(fileText);
            panel.Children.Add(sizeBlock);

            // Parent the panel to the border
            border.Child = panel;

            // Parent the border to the file label list
            fileLabels.Children.Add(border);
        }
    }

    private async Task InitServerConnection()
    {
        // https://stackoverflow.com/questions/40616911/c-sharp-udp-broadcast-and-receive-example
        // Use UDP broadcasting to send data across the local network, 
        // which the server will receive and respond with the server's ip address
        UdpClient udpClient = new UdpClient()
        {
            EnableBroadcast = true
        };

        byte[] message = Encoding.UTF8.GetBytes("SERVER_ADDRESS");

        IPEndPoint broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _clientUtility.ServerAddressPort);

        while (true)
        {
            try
            {
                // Send out a UDP broadcast to the local network, which the server is listening on
                await udpClient.SendAsync(message, message.Length, broadcastEndpoint);

                // Once the server intercepts the broadcast and returns the local server IP address, 
                // decode it and parse it into a valid IPAddress object
                var data = await udpClient.ReceiveAsync();
                string serverAddressStr = Encoding.UTF8.GetString(data.Buffer);
                IPAddress serverAddress = IPAddress.Parse(serverAddressStr);

                // Once we have a valid IP address, we can use it to establish a TCP connection to the server
                _client = new TcpClient();
                var ipEndpoint = new IPEndPoint(serverAddress, _clientUtility.ServerPort);
                Console.WriteLine(serverAddress);
                Console.WriteLine(_clientUtility.ServerPort);
                await _client.ConnectAsync(ipEndpoint);

                // Now that we've established a connection to the server, retrieve the network stream from the TCP connection
                NetworkStream stream = _client.GetStream();

                // Retrieve the port from the local TCP listener
                // This is used to tell the server to inform other clients that we've connected to it
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                
                // Create a new connection with the user's local IP address, port, and host name
                Connection userConnection = new Connection($"{_clientUtility.GetLocalIPv4Address()}", port, Dns.GetHostName());

                // Serialize it to JSON and encode it to UTF8 to send raw bytes over the stream
                string serializedData = JsonSerializer.Serialize(userConnection, SourceGenerationContext.Default.Connection);
                byte[] payload = _clientUtility.CreatePayload(Command.GetConnections, Encoding.UTF8.GetBytes(serializedData));

                // Write the payload to the stream
                await stream.WriteAsync(payload);
            
                // Now we let the client utility part of the application take over and handle incoming commands from the server or other clients
                await _clientUtility.HandleConnectedClient(_client);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);

                // Wait one second before retrying
                await Task.Delay(1000);
            }
        }
    }

    private async Task HandleConnectingClients()
    {
        // Similarly to the server side of things, 
        // we create a never ending loop which listens for clients attempting to connect to the local client
        while (true)
        {
            try
            {   
                // Wait for a connection to be made
                TcpClient client = await _listener.AcceptTcpClientAsync();

                // And just like the client-to-server connection, 
                // we let the client utility side of things handle incoming commands from other peers
                _ = Task.Run(() => _clientUtility.HandleConnectedClient(client));
                
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine(_clientUtility.ConnectionPort);
            }
        }
    }

    private async Task SendFiles(NetworkStream networkStream)
    {
        // Allows the user to select multiple files to send over
        // Get the TopLevel so we can utilize the systems file explorer
        TopLevel? topLevel = GetTopLevel(this);
        
        // Check if it exists first, then invoke the Dispatcher to prompt the user for a valid directory
        if (topLevel?.StorageProvider is { } storageProvider)
        {
            var files = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                return await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select files to upload",
                    AllowMultiple = true,
                });
            });

            // Send each file over to the connected client/server individually
            foreach (var file in files)
            {
                using (Stream stream = await file.OpenReadAsync())
                {
                    await _clientUtility.SendFile(networkStream, stream, file.Name);
                }
            }
        }
    }

    private async Task OnConnectionAdded(Connection newConnection)
    {
        // When a new connection is added, we need to start a new client that attempts to connect to the connection
        TcpClient client = new TcpClient();
        // Use the information provided from the connection to create an IPEndPoint object and use that to connect to the new client
        IPEndPoint ipEndpoint = IPEndPoint.Parse(newConnection.ConnectionAddress + ":" + newConnection.ConnectionPort);
        await client.ConnectAsync(ipEndpoint);

        // Get the new clients stream and set the connection's stream to the client's stream
        NetworkStream stream = client.GetStream();
        newConnection.ConnectionStream = stream;

        // Post a new UIThread to the dispatcher which allows us to update the client's list
        Dispatcher.UIThread.Post(() =>
        {
            // Where we're storing the list of connections in the interface
            StackPanel? connectionList = this.FindControl<StackPanel>("ConnectionList");

            // If it's not found, return early
            if (connectionList is null)
                return;

            // Create a button which allows us to send files to other users
            Button connectionInfo = new Button
            {
                Tag = newConnection, // Store the connection within the control's tag
                Content = $"Send files to {newConnection.ConnectionName}",
            };

            // Create an event listener for the button
            connectionInfo.Click += async (_, _) =>
            {
                
                // Double check to make sure the stream isn't null
                if (newConnection.ConnectionStream is null)
                {
                    return;
                }

                // Attempt to send files to the user
                try
                {
                    await SendFiles(newConnection.ConnectionStream);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            };

            // Add the button to the connected clients panel
            connectionList.Children.Add(connectionInfo);
        });
    }
    
    private void OnConnectionRemoved(Connection oldConnection)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StackPanel? connectionList = this.FindControl<StackPanel>("ConnectionList");

            if (connectionList is null)
                return;
            
            // Find any controls with a tag that matches the connection
            Control? connectionControl = connectionList.Children.FirstOrDefault(control => control.Tag == oldConnection);

            if (connectionControl is not null)  
            {
                // If we've found a valid connection, remove the control from the connection list
                connectionList.Children.Remove(connectionControl);
            }
        });
    }

    private async void SendFilesToServer(object? sender, RoutedEventArgs e)
    {
        // Sends files from the client over to the server
        if (_client is null)
            return;

        await SendFiles(_client.GetStream());
    }
}