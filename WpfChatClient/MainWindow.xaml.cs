using System;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WpfChatClient
{
    public partial class MainWindow : Window
    {
        private Socket _clientSocket;
        private bool _isConnected = false;
        private string _username;

        private Popup emojiPopup;
        private ObservableCollection<string> _onlineUsers = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();
            lstUsers.ItemsSource = _onlineUsers;
            lblUserCount.Text = "Users Online: 0";
        }

        private void AddMessage(string username, string message, string messageType)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            string displayMessage = "";

            // Chặn tất cả message từ "System" 
            if (username == "System") return;

            switch (messageType)
            {
                case "message":
                    displayMessage = $"[{timestamp}] {username}: {message}";
                    break;

                case "join":
                    displayMessage = $"[{timestamp}] {username} joined the chat.";
                    // Chỉ thêm user thật vào danh sách, không thêm "System"
                    if (username != "System" && !_onlineUsers.Contains(username))
                        _onlineUsers.Add(username);
                    break;

                case "leave":
                    displayMessage = $"[{timestamp}] {username} left the chat.";
                    _onlineUsers.Remove(username);
                    break;

                case "userlist":
                    // Xử lý danh sách user từ server
                    if (username == "ServerUserList")
                    {
                        _onlineUsers.Clear();
                        if (!string.IsNullOrEmpty(message))
                        {
                            var users = message.Split(',');
                            foreach (var user in users)
                            {
                                if (!string.IsNullOrWhiteSpace(user))
                                    _onlineUsers.Add(user.Trim());
                            }
                        }
                        lblUserCount.Text = $"Users Online: {_onlineUsers.Count}";
                        return; // Không hiển thị message này trong chat
                    }
                    break;

                case "system":
                    displayMessage = $"[{timestamp}] {message}";
                    break;

                case "error":
                    displayMessage = $"[{timestamp}] ERROR: {message}";
                    break;

                default:
                    return; // Bỏ qua message type không xác định
            }

            // Chỉ hiển thị message nếu có nội dung
            if (!string.IsNullOrEmpty(displayMessage))
            {
                lblUserCount.Text = $"Users Online: {_onlineUsers.Count}";
                txtMessages.Text += displayMessage + Environment.NewLine;
                scrollViewer.ScrollToEnd();
            }
        }
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Please enter a username!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _username = txtUsername.Text.Trim();
                var serverIP = txtServer.Text.Trim();
                var port = int.Parse(txtPort.Text.Trim());

                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await ConnectAsync(_clientSocket, serverIP, port);

                _isConnected = true;
                UpdateUI(true);

                // Chỉ thêm chính mình vào danh sách, không thêm "System"
                AddMessage(_username, "", "join");

                // Gửi thông điệp join lên server để broadcast cho những người khác
                var joinMessage = new ChatMessage
                {
                    Username = _username,
                    Message = "",
                    MessageType = "join"
                };
                await SendMessageAsync(joinMessage);

                _ = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendCurrentMessage();
        }

        private async void TxtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await SendCurrentMessage();
        }

        private async Task SendCurrentMessage()
        {
            if (!_isConnected || string.IsNullOrWhiteSpace(txtMessage.Text))
                return;

            try
            {
                var message = new ChatMessage
                {
                    Username = _username,
                    Message = txtMessage.Text.Trim(),
                    MessageType = "message"
                };

                await SendMessageAsync(message);
                txtMessage.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to send message: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ConnectAsync(Socket socket, string host, int port)
        {
            await Task.Factory.FromAsync(
                (callback, state) => socket.BeginConnect(host, port, callback, state),
                socket.EndConnect,
                null);
        }

        private async Task SendMessageAsync(ChatMessage message)
        {
            var messageJson = JsonSerializer.Serialize(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            await Task.Factory.FromAsync(
                (callback, state) => _clientSocket.BeginSend(messageBytes, 0, messageBytes.Length, SocketFlags.None, callback, state),
                _clientSocket.EndSend,
                null);
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];

            try
            {
                while (_isConnected && _clientSocket.Connected)
                {
                    var received = await Task.Factory.FromAsync<int>(
                        (callback, state) => _clientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, callback, state),
                        _clientSocket.EndReceive,
                        null);

                    if (received == 0) break;

                    var messageJson = Encoding.UTF8.GetString(buffer, 0, received);
                    var chatMessage = JsonSerializer.Deserialize<ChatMessage>(messageJson);

                    if (chatMessage != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AddMessage(chatMessage.Username, chatMessage.Message, chatMessage.MessageType);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AddMessage("System", $"Connection lost: {ex.Message}", "error");
                        Disconnect();
                    });
                }
            }
        }

        private void UpdateUI(bool connected)
        {
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            txtMessage.IsEnabled = connected;
            btnSend.IsEnabled = connected;
            btnEmoji.IsEnabled = connected;
            txtServer.IsEnabled = !connected;
            txtPort.IsEnabled = !connected;
            txtUsername.IsEnabled = !connected;

            lblStatus.Text = connected ? $"Connected as {_username}" : "Disconnected";
        }

        private void Disconnect()
        {
            try
            {
                _isConnected = false;
                _clientSocket?.Close();
                _onlineUsers.Clear();
                lblUserCount.Text = "Users Online: 0";

                UpdateUI(false);
                AddMessage("System", "Disconnected from server", "system");
            }
            catch (Exception ex)
            {
                AddMessage("System", $"Error during disconnect: {ex.Message}", "error");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Disconnect();
            base.OnClosed(e);
        }

        private void BtnEmoji_Click(object sender, RoutedEventArgs e)
        {
            if (emojiPopup == null)
            {
                emojiPopup = new Popup
                {
                    PlacementTarget = btnEmoji,
                    Placement = PlacementMode.Top,
                    StaysOpen = false
                };

                UniformGrid grid = new UniformGrid { Rows = 5, Columns = 6 };
                string[] emojis = { "😀","😂","😍","😭","👍","🎉","❤️","😎","😢","🤔","🙌","👏",
                                    "🔥","🥳","😴","😡","😇","🎂","💯","🎶","🤯","😅","🤩","🥺",
                                    "😱","😋","🙏","😉","😏","💀" };

                foreach (var emoji in emojis)
                {
                    Button btn = new Button
                    {
                        Content = emoji,
                        FontSize = 18,
                        FontFamily = new FontFamily("Segoe UI Emoji"),
                        Margin = new Thickness(2),
                        Padding = new Thickness(5)
                    };
                    btn.Click += (s, args) =>
                    {
                        txtMessage.Text += emoji;
                        txtMessage.Focus();
                        txtMessage.CaretIndex = txtMessage.Text.Length;
                        emojiPopup.IsOpen = false;
                    };
                    grid.Children.Add(btn);
                }

                emojiPopup.Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Child = grid
                };
            }

            emojiPopup.IsOpen = true;
        }
    }
}
