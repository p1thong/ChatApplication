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
using System.IO;

namespace WpfChatClient
{
    public partial class MainWindow : Window
    {
        private Socket _clientSocket;
        private bool _isConnected = false;
        private string _username;

        private Popup emojiPopup;
        private ObservableCollection<string> _onlineUsers = new ObservableCollection<string>();

        // ==== Biến nhận file ====
        private string _receivingFile;
        private MemoryStream _receivedFileBuffer;
        private long _expectedFileSize;
        private long _bytesReceived;

        // UI cho phần nhận file
        private StackPanel _currentFilePanel;
        private ProgressBar _currentFileProgress;
        private Button _currentSaveButton;

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

            switch (messageType)
            {
                case "message":
                    displayMessage = $"[{timestamp}] {username}: {message}";
                    break;

                case "join":
                    displayMessage = $"[{timestamp}] {username} joined the chat.";
                    if (username != "System" && !_onlineUsers.Contains(username))
                        _onlineUsers.Add(username);
                    lblUserCount.Text = $"Users Online: {_onlineUsers.Count}"; // ✅ cập nhật số lượng
                    break;

                case "leave":
                    displayMessage = $"[{timestamp}] {username} left the chat.";
                    _onlineUsers.Remove(username);
                    lblUserCount.Text = $"Users Online: {_onlineUsers.Count}"; // ✅ cập nhật số lượng
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
                Dispatcher.Invoke(() =>
                {
                    var textBlock = new TextBlock
                    {
                        Text = displayMessage,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new FontFamily("Segoe UI Emoji"),
                        Margin = new Thickness(5, 2, 5, 2)
                    };
                    chatPanel.Children.Add(textBlock);
                    ScrollToBottom();
                });
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

                // Gửi thông điệp join lên server để broadcast
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

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            try
            {
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

                        try
                        {
                            var chatMessage = JsonSerializer.Deserialize<ChatMessage>(singleJson);
                            if (chatMessage == null) continue;

                            // === Nhận thông tin file ===
                            if (chatMessage.MessageType == "fileinfo")
                            {
                                var parts = chatMessage.Message.Split('|');
                                if (parts.Length < 2 || !long.TryParse(parts[1], out long fileSize))
                                {
                                    Dispatcher.Invoke(() => AddMessage("System", "Invalid file info format!", "error"));
                                    continue;
                                }

                                _receivingFile = parts[0];
                                _expectedFileSize = fileSize;
                                _bytesReceived = 0;
                                _receivedFileBuffer = new MemoryStream();

                                Dispatcher.Invoke(() =>
                                {
                                    ShowFileReceivingUI(_receivingFile, _expectedFileSize);
                                    lblStatus.Text = $"Receiving {_receivingFile}...";
                                });

                                continue;
                            }

                            // === Nhận chunk dữ liệu file ===
                            if (chatMessage.MessageType == "filechunk")
                            {
                                var chunkMsg = JsonSerializer.Deserialize<FileChunkMessage>(singleJson);
                                if (chunkMsg?.Data == null || _receivedFileBuffer == null) continue;

                                byte[] fileBytes = Convert.FromBase64String(chunkMsg.Data);
                                await _receivedFileBuffer.WriteAsync(fileBytes, 0, fileBytes.Length);
                                _bytesReceived += fileBytes.Length;

                                Dispatcher.Invoke(() =>
                                {
                                    UpdateFileProgress(_bytesReceived);
                                    lblStatus.Text = $"Downloading {_receivingFile}: {_bytesReceived * 100 / _expectedFileSize}%";
                                });

                                if (_bytesReceived >= _expectedFileSize)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        lblStatus.Text = $"Download complete: {_receivingFile}";
                                        EnableSaveButton();
                                        AddMessage("System", $"File {_receivingFile} ready to save", "system");
                                    });
                                }

                                continue;
                            }

                            Dispatcher.Invoke(() => AddMessage(chatMessage.Username, chatMessage.Message, chatMessage.MessageType));
                        }
                        catch (JsonException ex)
                        {
                            Dispatcher.Invoke(() => AddMessage("System", $"JSON error: {ex.Message}", "error"));
                        }
                    }

                    sb.Clear();
                    sb.Append(data);
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

        // === UI hiển thị khi nhận file ===
        private void ShowFileReceivingUI(string fileName, long fileSize)
        {
            _currentFilePanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(5) };

            var txt = new TextBlock
            {
                Text = $"Receiving file: {fileName} ({fileSize / 1024.0 / 1024.0:F2} MB)",
                Foreground = Brushes.Blue
            };

            _currentFileProgress = new ProgressBar
            {
                Minimum = 0,
                Maximum = fileSize,
                Height = 10,
                Margin = new Thickness(0, 5, 0, 5)
            };

            _currentSaveButton = new Button
            {
                Content = $"💾 Save {fileName}",
                Visibility = Visibility.Collapsed,
                IsEnabled = false,
                Margin = new Thickness(0, 5, 0, 0)
            };
            _currentSaveButton.Click += SaveReceivedFile_Click;

            _currentFilePanel.Children.Add(txt);
            _currentFilePanel.Children.Add(_currentFileProgress);
            _currentFilePanel.Children.Add(_currentSaveButton);

            chatPanel.Children.Add(_currentFilePanel);
            ScrollToBottom();
        }

        private void UpdateFileProgress(long receivedBytes)
        {
            if (_currentFileProgress != null)
                _currentFileProgress.Value = receivedBytes;
        }

        private void EnableSaveButton()
        {
            if (_currentSaveButton != null)
            {
                _currentSaveButton.Visibility = Visibility.Visible;
                _currentSaveButton.IsEnabled = true;
            }
        }

        private void SaveReceivedFile_Click(object sender, RoutedEventArgs e)
        {
            if (_receivedFileBuffer == null || _receivedFileBuffer.Length == 0)
            {
                AddMessage("System", "No file to save!", "error");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _receivingFile,
                Filter = "All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllBytes(dialog.FileName, _receivedFileBuffer.ToArray());
                AddMessage("System", $"File saved to: {dialog.FileName}", "system");
            }

            // _currentSaveButton.IsEnabled = false;
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

        private async void BtnSendFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                string filePath = dialog.FileName;
                string fileName = System.IO.Path.GetFileName(filePath);
                long fileSize = new System.IO.FileInfo(filePath).Length;

                var fileInfoMessage = new ChatMessage
                {
                    Username = _username,
                    MessageType = "fileinfo",
                    Message = $"{fileName}|{fileSize}"
                };
                await SendMessageAsync(fileInfoMessage);

                byte[] buffer = new byte[256 * 1024];
                using (var fs = System.IO.File.OpenRead(filePath))
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
                            lblStatus.Text = $"Uploading {fileName}: {sent * 100 / fileSize}%";
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = $"Upload complete: {fileName}";
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
        private void ScrollToBottom()
        {
            Dispatcher.Invoke(() =>
            {
                scrollViewer.ScrollToEnd();
            });
        }

    }
}
