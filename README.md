# Netport - A Simple Network Based File Sharing Application

This was a project that I made to help me understand more about the network protocols we rely on every day.
It makes use of both TCP (Transfer Control Protocol) and UDP (User Datagram Protocol) technologies 
to send data over to a server and connected clients. Once the server is online and a client needs to connect to it, 
the client will attempt to send a UDP broadcast to the server, which will respond with the server's IPv4 address.
When the client receives the server's address, it'll attempt to establish a TCP connection to it. Once the client has connected,
the server will store its data and send it to other connected clients for peer 2 peer connections.

The application uses a command protocol which requires a message to start with [command (byte)]\[payload size (ulong)]\[payload byte[]]
This tells the application what command to perform, how many bytes are needed to be read, and reads the payload content.

## Instructions for Build and Use

[Application Demo](https://youtu.be/X7y5lvikKuE)

Steps to build and/or run the software:

1. Ensure the .NET 9 SDK is installed on your system (this can be checked by running the command `dotnet --version` in the terminal)
2. If it's not installed, install the .NET 9 SDK [here](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
3. Download the repository and open it.

### Building the Server

1. To build the server, open a terminal in the repository's directory
2. Run `dotnet build --project NetportServer` or if the first one doesn't work, `dotnet build .\NetportServer\NetportServer.csproj` in the terminal
3. To start the server, run `dotnet run --project NetportServer` in the terminal

### Building the Client

1. To build the client, open a terminal in the same directory
2. Run `dotnet build --project NetportClient` or if the first one doesn't work, `dotnet build .\NetportClient\NetportClient.csproj` in the terminal
3. To start the client, run `dotnet run --project NetportClient` in the terminal

Instructions for using the software:

1. Start up the server
2. Start up the client
3. Select the "Upload" button in the top left corner of the interface to upload files to the server
4. If any files are on the server and are displayed on the client, click the "Download" button next to the file name to download it. A relative history of downloads will be shown on the right of the screen, and will display downloaded progress.
5. If there are any files displayed on the client, click the "Delete" button next to the file name to delete it from the server
6. If any other clients are connected, their device's name will show up under the "Connected Clients" section. Click on the clients name to send a file over to their device, but only if they let you.

## Development Environment

To recreate the development environment, you need the following software and/or libraries with the specified versions:

* .NET 9 SDK v9.0.307
* Visual Studio Code v1.109.3
* Jetbrains Rider v2025.3.1 for more integrated Avalonia development
* Avalonia v11.3.12

## Useful Websites to Learn More

I found these websites useful in developing this software:

* [How to use source generation in System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
* [Converting bytes to GB in C#](https://stackoverflow.com/a/2082893)
* [TCP overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/sockets/tcp-classes)
* [Use UDP services](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/using-udp-services)
* [C# UDP Broadcast and receive example](https://stackoverflow.com/questions/40616911/c-sharp-udp-broadcast-and-receive-example)
* [Message Framing](https://blog.stephencleary.com/2009/04/message-framing.html)

## Future Work

The following items I plan to fix, improve, and/or add to this project in the future:

* [ ] Fix bug that freezes the server when user cancels a download from the server
* [ ] Fix issue where small downloaded files sometimes display "0%" instead of "Complete"
* [ ] Initialize a new connection specifically for file transfers since they need to finish before other actions can be performed
* [ ] Make message protocol more robust against interruptions