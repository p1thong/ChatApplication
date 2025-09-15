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

            try
            {
                while (clientSocket.Connected)
                {
                    var received = await ReceiveAsync(clientSocket, buffer);
                    if (received == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, received);
                    var chatMessage = JsonSerializer.Deserialize<ChatMessage>(messageJson);

                    if (chatMessage != null)
                    {
                        if (chatMessage.MessageType == "join")
                        {
                            clientInfo.Username = chatMessage.Username;
                            _clients.TryAdd(clientInfo.Id, clientInfo);
                            _logger.LogInformation($"User {chatMessage.Username} joined the chat");

                            // Gửi message join với username thật, không phải "System"
                            await BroadcastMessageAsync(new ChatMessage
                            {
                                Username = chatMessage.Username,
                                Message = "",
                                MessageType = "join"
                            });

                            // Gửi danh sách tất cả users hiện tại cho client mới join
                            await SendUserListToAllClientsAsync();
                        }
                        else if (chatMessage.MessageType == "message")
                        {
                            _logger.LogInformation($"Message from {chatMessage.Username}: {chatMessage.Message}");
                            await BroadcastMessageAsync(chatMessage);
                        }
                    }
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

                    // Gửi message leave với username thật
                    await BroadcastMessageAsync(new ChatMessage
                    {
                        Username = clientInfo.Username,
                        Message = "",
                        MessageType = "leave"
                    });

                    // Cập nhật danh sách user sau khi có người rời
                    await SendUserListToAllClientsAsync();
                }

                clientSocket.Close();
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