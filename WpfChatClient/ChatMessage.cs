using System;

namespace WpfChatClient
{
    public class ChatMessage
    {
        public string Username { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string MessageType { get; set; } = "message";
    }
}