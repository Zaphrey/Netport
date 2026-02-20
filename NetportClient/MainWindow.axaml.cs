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
    private int _serverPort;
    // Port 0 leaves it up to the os to decide
    private TcpListener _listener;
    private IPAddress? serverAddress;
    
    public MainWindow()
    {
        _clientUtility.TopLevel = GetTopLevel(this);
        InitializeComponent();
        _clientUtility.DeviceName = "CLIENT_" + Dns.GetHostName();
        _serverPort = _clientUtility.ConnectionPort;
        
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
        
        _listener = new(_clientUtility.GetLocalIPv4Address(), 0);
        _listener.Start();
        
        _clientUtility.ConnectionPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        
        Task.Run(InitServerConnection);
        Task.Run(HandleConnectingClients);
    }

    private void AddFileToDownloads(string fileName)
    {
        var downloads = this.FindControl<StackPanel>("DownloadPanel");

        if (downloads is null)
            return;
        
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

        void UpdateDownloadProgress(object? sender, (string fileName, double progress) args)
        {
            Console.WriteLine(args);
            if (args.fileName != fileName)
                return;
            
            Dispatcher.UIThread.Post(() =>
            {
                downloadProgress.Text = $"{Math.Round(args.progress, 2)}%";
            });
        }

        _clientUtility.DownloadProgressUpdate += UpdateDownloadProgress;

        void DownloadStopped(object? sender, (string fileName, bool completed) args)
        {
            if (args.fileName != fileName)
                return;
            
            Dispatcher.UIThread.Post(() =>
            {
                downloadProgress.Text = $"{ (args.completed ? "Completed" : "Failed") }";
            });
            
            _clientUtility.DownloadProgressUpdate -= UpdateDownloadProgress;
            _clientUtility.DownloadStopped -= DownloadStopped;
        }

        _clientUtility.DownloadStopped += DownloadStopped;
        
        
        panel.Children.Add(nameBlock);
        panel.Children.Add(downloadProgress);
        downloads.Children.Add(panel);
    }
    
    private void PopulateFileEntries()
    {
        var fileLabels = this.FindControl<StackPanel>("FileLabels");

        if (fileLabels is null)
            return;

        fileLabels.Children.Clear();
            
        foreach (FileListEntry entry in _clientUtility.FileEntries)
        {
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

            downloadButton.Click += async (_, _) =>
            {
                byte[] fileNameBytes = Encoding.UTF8.GetBytes(entry.Name);
                byte[] payload = _clientUtility.CreatePayload(Command.FileRequest, fileNameBytes);

                NetworkStream? stream = _client?.GetStream();

                if (stream is not null)
                    await stream.WriteAsync(payload);
            };
            
            panel.Children.Add(downloadButton);
            panel.Children.Add(deleteButton);
            panel.Children.Add(fileText);
            panel.Children.Add(sizeBlock);

            border.Child = panel;
            
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

        IPEndPoint broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _serverPort);
        
        while (true)
        {
            try
            {
                await udpClient.SendAsync(message, message.Length, broadcastEndpoint);

                var data = await udpClient.ReceiveAsync();
                string serverAddressStr = Encoding.UTF8.GetString(data.Buffer);
                IPAddress serverAddress = IPAddress.Parse(serverAddressStr);


                _client = new TcpClient();
                var ipEndpoint = new IPEndPoint(serverAddress, _serverPort);
                await _client.ConnectAsync(ipEndpoint);
                NetworkStream stream = _client.GetStream();
            
                Console.WriteLine("Client connected");
                Console.WriteLine("Sending connection data to server");
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                
                Connection userConnection =
                    new Connection($"{_clientUtility.GetLocalIPv4Address()}", port, Dns.GetHostName());

                string serializedData = JsonSerializer.Serialize(userConnection);
            
                byte[] payload = _clientUtility.CreatePayload(Command.GetConnections, Encoding.UTF8.GetBytes(serializedData));
                await stream.WriteAsync(payload);
            
                Console.WriteLine("Connection data sent");
                await _clientUtility.HandleConnectedClient(_client, isServer: false);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }
    }

    private async Task HandleConnectingClients()
    {
        while (true)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => _clientUtility.HandleConnectedClient(client, isServer: false));
                
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
        TopLevel? topLevel = GetTopLevel(this);

        if (topLevel?.StorageProvider is { } storageProvider)
        {

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select files to upload",
                AllowMultiple = true,
            });
            
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
        Console.WriteLine($"New connection: {newConnection.ConnectionName} at {newConnection.ConnectionAddress}:{newConnection.ConnectionPort}");
        Console.WriteLine(newConnection.GetHashCode());

        await ConnectToPeer(newConnection);
        
        Dispatcher.UIThread.Post(() =>
        {
            StackPanel? connectionList = this.FindControl<StackPanel>("ConnectionList");

            if (connectionList is null)
                return;

            Button connectionInfo = new Button
            {
                Tag = newConnection,
                Content =
                    $"{newConnection.ConnectionName} on port {newConnection.ConnectionPort}",
            };
            Console.WriteLine(newConnection.GetHashCode());
            connectionInfo.Click += async (_, _) =>
            {
                Console.WriteLine(newConnection.GetHashCode());
                if (newConnection.ConnectionStream is null)
                {
                    return;
                }

                try
                {
                    // await _clientUtility.SendMessage(newConnection.ConnectionStream, "Bop it");
                    await SendFiles(newConnection.ConnectionStream);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }

                Console.WriteLine("FGH");
            };

            connectionList.Children.Add(connectionInfo);
        });
    }

    private async Task ConnectToPeer(Connection connection)
    {
        TcpClient client = new TcpClient();
        var ipEndpoint = IPEndPoint.Parse(connection.ConnectionAddress + ":" + connection.ConnectionPort);
        await client.ConnectAsync(ipEndpoint);
        NetworkStream stream = client.GetStream();
        connection.ConnectionStream = stream;
        // // await _clientUtility.HandleConnectedClient(client, isServer: false);
        // return client;
    }
    
    private void OnConnectionRemoved(Connection oldConnection)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StackPanel? connectionList = this.FindControl<StackPanel>("ConnectionList");

            if (connectionList is null)
                return;
            
            // Remove the client's info from the list
            Control? connectionControl = connectionList.Children.FirstOrDefault(control => control.Tag == oldConnection);

            if (connectionControl is not null)
            {
                connectionList.Children.Remove(connectionControl);
            }
        });
    }

    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_client is null)
            return;

        await SendFiles(_client.GetStream());
    }
}