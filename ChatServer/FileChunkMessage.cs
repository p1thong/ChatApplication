namespace ChatServer
{
    public class FileChunkMessage : ChatMessage
    {
        public string FileName { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty; // Base64 encoded chunk
    }
}
