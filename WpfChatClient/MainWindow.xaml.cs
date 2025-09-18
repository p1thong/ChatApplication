using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.IO;

namespace WpfChatClient
{
    public partial class MainWindow : Window
    {
        private Socket _clientSocket;
        private bool _isConnected = false;
        private string _username;

        private Popup emojiPopup;
        private ObservableCollection<string> _onlineUsers = new();

        // ==== Gửi file UI ====
        private StackPanel _currentFilePanel;
        private ProgressBar _currentFileProgress;

        // ==== Nhận file ====
        private Dictionary<string, FileTransferContext> _fileTransfers = new();

        public MainWindow()
        {
            InitializeComponent();
            lstUsers.ItemsSource = _onlineUsers;
            lblUserCount.Text = "Users Online: 0";
        }

        // ===================== CHAT =====================
        private void AddMessage(string username, string message, string messageType)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            string displayMessage = "";

            switch (messageType)
            {
                case "message":
                    displayMessage = $"[{timestamp}] {username}: {message}";
                    break;

                case "join":
                    displayMessage = $"[{timestamp}] {username} joined the chat.";
                    if (username != "System" && !_onlineUsers.Contains(username))
                        _onlineUsers.Add(username);
                    lblUserCount.Text = $"Users Online: {_onlineUsers.Count}";
                    break;

                case "leave":
                    displayMessage = $"[{timestamp}] {username} left the chat.";
                    _onlineUsers.Remove(username);
                    lblUserCount.Text = $"Users Online: {_onlineUsers.Count}";
                    break;

                case "userlist":
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
                        return; // không in ra chat
                    }
                    break;

                case "system":
                    displayMessage = $"[{timestamp}] {message}";
                    break;

                case "error":
                    displayMessage = $"[{timestamp}] ERROR: {message}";
                    break;

                default:
                    return;
            }

            if (!string.IsNullOrEmpty(displayMessage))
            {
                lblUserCount.Text = $"Users Online: {_onlineUsers.Count}";
                var textBlock = new TextBlock
                {
                    Text = displayMessage,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    Margin = new Thickness(5, 2, 5, 2)
                };
                chatPanel.Children.Add(textBlock);
                ScrollToBottom();
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

                // Gửi join message
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

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

        private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendCurrentMessage();

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
            var messageJson = JsonSerializer.Serialize(message) + "\n";
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            await Task.Factory.FromAsync(
                (callback, state) => _clientSocket.BeginSend(messageBytes, 0, messageBytes.Length, SocketFlags.None, callback, state),
                _clientSocket.EndSend,
                null);
        }

        // ===================== RECEIVE =====================
        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            while (_isConnected && _clientSocket.Connected)
            {
                int received = await Task.Factory.FromAsync<int>(
                    (cb, state) => _clientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, cb, state),
                    _clientSocket.EndReceive, null);

                if (received == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, received));
                string data = sb.ToString();

                int newlineIndex;
                while ((newlineIndex = data.IndexOf('\n')) >= 0)
                {
                    string singleJson = data[..newlineIndex];
                    data = data[(newlineIndex + 1)..];

                    var chatMessage = JsonSerializer.Deserialize<ChatMessage>(singleJson);
                    if (chatMessage == null) continue;

                    if (chatMessage.MessageType == "fileinfo")
                    {
                        var parts = chatMessage.Message.Split('|');
                        if (parts.Length < 2 || !long.TryParse(parts[1], out long fileSize)) continue;

                        // Bỏ qua chính mình
                        if (chatMessage.Username == _username) continue;

                        var ctx = new FileTransferContext
                        {
                            FileName = parts[0],
                            FileSize = fileSize
                        };

                        _fileTransfers[ctx.FileName] = ctx;

                        Dispatcher.Invoke(() =>
                        {
                            ShowFileReceivingUI(ctx);
                            lblStatus.Text = $"Receiving {ctx.FileName}...";
                        });

                        continue;
                    }

                    if (chatMessage.MessageType == "filechunk")
                    {
                        var chunkMsg = JsonSerializer.Deserialize<FileChunkMessage>(singleJson);
                        if (chunkMsg == null) continue;

                        if (!_fileTransfers.TryGetValue(chunkMsg.FileName, out var ctx)) continue;

                        byte[] fileBytes = Convert.FromBase64String(chunkMsg.Data);
                        await ctx.Buffer.WriteAsync(fileBytes, 0, fileBytes.Length);
                        ctx.BytesReceived += fileBytes.Length;

                        Dispatcher.Invoke(() =>
                        {
                            ctx.Progress.Value = ctx.BytesReceived;
                            lblStatus.Text = $"Receiving {ctx.FileName}: {ctx.BytesReceived * 100 / ctx.FileSize}%";
                        });

                        if (ctx.BytesReceived >= ctx.FileSize)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                lblStatus.Text = $"Download complete: {ctx.FileName}";
                                ctx.SaveButton.Visibility = Visibility.Visible;
                                ctx.SaveButton.IsEnabled = true;
                            });
                        }

                        continue;
                    }

                    Dispatcher.Invoke(() => AddMessage(chatMessage.Username, chatMessage.Message, chatMessage.MessageType));
                }

                sb.Clear();
                sb.Append(data);
            }
        }

        // ===================== UI FILE RECEIVE =====================
        private void ShowFileReceivingUI(FileTransferContext ctx)
        {
            ctx.FilePanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(5) };

            var txt = new TextBlock
            {
                Text = $"Receiving file: {ctx.FileName} ({ctx.FileSize / 1024.0 / 1024.0:F2} MB)",
                Foreground = Brushes.Blue
            };

            ctx.Progress = new ProgressBar
            {
                Minimum = 0,
                Maximum = ctx.FileSize,
                Height = 10,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.Blue
            };

            ctx.SaveButton = new Button
            {
                Content = $"💾 Save {ctx.FileName}",
                Visibility = Visibility.Collapsed,
                IsEnabled = false,
                Margin = new Thickness(0, 5, 0, 0)
            };
            ctx.SaveButton.Click += (s, e) => SaveReceivedFile(ctx);

            ctx.FilePanel.Children.Add(txt);
            ctx.FilePanel.Children.Add(ctx.Progress);
            ctx.FilePanel.Children.Add(ctx.SaveButton);

            chatPanel.Children.Add(ctx.FilePanel);
            ScrollToBottom();
        }

        private void SaveReceivedFile(FileTransferContext ctx)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = ctx.FileName,
                Filter = "All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllBytes(dialog.FileName, ctx.Buffer.ToArray());
                AddMessage("System", $"File saved to: {dialog.FileName}", "system");
            }
        }

        // ===================== UI FILE SEND =====================
        private void ShowFileSendingUI(string fileName, long fileSize)
        {
            _currentFilePanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(5) };

            var txt = new TextBlock
            {
                Text = $"Uploading file: {fileName} ({fileSize / 1024.0 / 1024.0:F2} MB)",
                Foreground = Brushes.OrangeRed
            };

            _currentFileProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = fileSize,
                Height = 10,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.OrangeRed
            };

            _currentFilePanel.Children.Add(txt);
            _currentFilePanel.Children.Add(_currentFileProgress);

            chatPanel.Children.Add(_currentFilePanel);
            ScrollToBottom();
        }

        private async void BtnSendFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                string filePath = dialog.FileName;
                string fileName = System.IO.Path.GetFileName(filePath);
                long fileSize = new FileInfo(filePath).Length;

                Dispatcher.Invoke(() => ShowFileSendingUI(fileName, fileSize));

                // Gửi thông tin file
                var fileInfoMessage = new ChatMessage
                {
                    Username = _username,
                    MessageType = "fileinfo",
                    Message = $"{fileName}|{fileSize}"
                };
                await SendMessageAsync(fileInfoMessage);

                byte[] buffer = new byte[256 * 1024];
                using (var fs = File.OpenRead(filePath))
                {
                    int bytesRead;
                    long sent = 0;
                    while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        sent += bytesRead;
                        var chunkMsg = new FileChunkMessage
                        {
                            Username = _username,
                            FileName = fileName,
                            Data = Convert.ToBase64String(buffer, 0, bytesRead),
                            MessageType = "filechunk"
                        };

                        await SendFileChunkAsync(chunkMsg);

                        Dispatcher.Invoke(() =>
                        {
                            if (_currentFileProgress != null)
                                _currentFileProgress.Value = sent;

                            lblStatus.Text = $"Uploading {fileName}: {sent * 100 / fileSize}%";
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = $"Upload complete: {fileName}";
                    if (_currentFileProgress != null)
                        _currentFileProgress.Value = fileSize;

                    // reset biến để tránh ảnh hưởng lần gửi sau
                    _currentFilePanel = null;
                    _currentFileProgress = null;
                });
            }
        }

        private async Task SendFileChunkAsync(FileChunkMessage chunk)
        {
            var messageJson = JsonSerializer.Serialize(chunk) + "\n";
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            await Task.Factory.FromAsync(
                (callback, state) => _clientSocket.BeginSend(messageBytes, 0, messageBytes.Length, SocketFlags.None, callback, state),
                _clientSocket.EndSend,
                null);
        }

        // ===================== UI =====================
        private void ScrollToBottom()
        {
            Dispatcher.Invoke(() => scrollViewer.ScrollToEnd());
        }

        private void UpdateUI(bool connected)
        {
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            txtMessage.IsEnabled = connected;
            btnSend.IsEnabled = connected;
            btnEmoji.IsEnabled = connected;
            btnSendFile.IsEnabled = connected;
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

        // ===================== EMOJI =====================
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
