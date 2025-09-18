using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChatServer
{
    public class ChatSocketServer
    {
        private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
        private readonly ILogger<ChatSocketServer> _logger;
        private Socket _serverSocket;

        public ChatSocketServer(ILogger<ChatSocketServer> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync()
        {
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.Bind(new IPEndPoint(IPAddress.Any, 9000));
            _serverSocket.Listen(10);

            _logger.LogInformation("Chat server started on port 9000");

            while (true)
            {
                try
                {
                    var clientSocket = await AcceptAsync(_serverSocket);
                    _ = Task.Run(() => HandleClientAsync(clientSocket));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting client connection");
                }
            }
        }

        private async Task<Socket> AcceptAsync(Socket socket)
        {
            return await Task.Factory.FromAsync(socket.BeginAccept, socket.EndAccept, null);
        }

        private async Task HandleClientAsync(Socket clientSocket)
        {
            var clientInfo = new ClientInfo { Socket = clientSocket };
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            try
            {
                while (clientSocket.Connected)
                {
                    var received = await ReceiveAsync(clientSocket, buffer);
                    if (received == 0) break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, received));
                    string data = sb.ToString();

                    int newlineIndex;
                    while ((newlineIndex = data.IndexOf('\n')) >= 0)
                    {
                        string singleJson = data[..newlineIndex];
                        data = data[(newlineIndex + 1)..];

                        try
                        {
                            var chatMessage = JsonSerializer.Deserialize<ChatMessage>(singleJson);
                            if (chatMessage == null) continue;

                            switch (chatMessage.MessageType)
                            {
                                case "join":
                                    clientInfo.Username = chatMessage.Username;
                                    _clients.TryAdd(clientInfo.Id, clientInfo);
                                    _logger.LogInformation($"User {chatMessage.Username} joined");
                                    await BroadcastMessageAsync(new ChatMessage
                                    {
                                        Username = chatMessage.Username,
                                        MessageType = "join"
                                    });
                                    await SendUserListToAllClientsAsync();
                                    break;

                                case "message":
                                case "fileinfo":
                                    // message + fileinfo vẫn dùng ChatMessage
                                    await BroadcastMessageAsync(chatMessage);
                                    break;

                                case "filechunk":
                                    // 🔧 Deserialize lại thành FileChunkMessage để giữ Data
                                    var fileChunk = JsonSerializer.Deserialize<FileChunkMessage>(singleJson);
                                    if (fileChunk != null)
                                    {
                                        var json = JsonSerializer.Serialize(fileChunk) + "\n";
                                        var bytes = Encoding.UTF8.GetBytes(json);
                                        await BroadcastRawAsync(bytes);
                                    }
                                    break;

                                default:
                                    _logger.LogWarning($"Unknown message type: {chatMessage.MessageType}");
                                    break;
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, $"Invalid JSON: {singleJson}");
                        }
                    }

                    sb.Clear();
                    sb.Append(data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling client {clientInfo.Username}");
            }
            finally
            {
                if (_clients.TryRemove(clientInfo.Id, out _))
                {
                    _logger.LogInformation($"User {clientInfo.Username} disconnected");
                    await BroadcastMessageAsync(new ChatMessage
                    {
                        Username = clientInfo.Username,
                        MessageType = "leave"
                    });
                    await SendUserListToAllClientsAsync();
                }

                clientSocket.Close();
            }
        }

        private async Task BroadcastRawAsync(byte[] messageBytes)
        {
            _logger.LogInformation($"Broadcasting raw message ({messageBytes.Length} bytes)");

            var disconnectedClients = new List<string>();
            foreach (var client in _clients.Values)
            {
                try
                {
                    if (client.Socket.Connected)
                    {
                        await SendAsync(client.Socket, messageBytes);
                    }
                    else
                    {
                        disconnectedClients.Add(client.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending raw message to {client.Username}");
                    disconnectedClients.Add(client.Id);
                }
            }

            foreach (var id in disconnectedClients)
            {
                _clients.TryRemove(id, out _);
            }
        }



        private async Task SendUserListToAllClientsAsync()
        {
            var userList = _clients.Values.Select(c => c.Username).Where(u => !string.IsNullOrEmpty(u)).ToList();

            var userListMessage = new ChatMessage
            {
                Username = "ServerUserList",
                Message = string.Join(",", userList),
                MessageType = "userlist"
            };

            await BroadcastMessageAsync(userListMessage);
        }

        private async Task<int> ReceiveAsync(Socket socket, byte[] buffer)
        {
            return await Task.Factory.FromAsync<int>(
                (callback, state) => socket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, callback, state),
                socket.EndReceive,
                null);
        }

        private async Task BroadcastMessageAsync(ChatMessage message)
        {
            var messageJson = JsonSerializer.Serialize(message) + "\n"; // 👈 Thêm newline
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            _logger.LogInformation($"Broadcasting message: {messageJson}");

            var disconnectedClients = new List<string>();

            foreach (var client in _clients.Values)
            {
                try
                {
                    if (client.Socket.Connected)
                    {
                        await SendAsync(client.Socket, messageBytes);
                    }
                    else
                    {
                        disconnectedClients.Add(client.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending message to {client.Username}");
                    disconnectedClients.Add(client.Id);
                }
            }

            foreach (var clientId in disconnectedClients)
            {
                _clients.TryRemove(clientId, out _);
            }
        }

        private async Task SendAsync(Socket socket, byte[] data)
        {
            await Task.Factory.FromAsync(
                (callback, state) => socket.BeginSend(data, 0, data.Length, SocketFlags.None, callback, state),
                socket.EndSend,
                null);
        }

        public void Stop()
        {
            _serverSocket?.Close();
            foreach (var client in _clients.Values)
            {
                client.Socket.Close();
            }
        }
    }
}