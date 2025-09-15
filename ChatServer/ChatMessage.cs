namespace ChatServer
{
    public class ChatMessage
    {
        public string Username { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string MessageType { get; set; } = "message";
    }

}
