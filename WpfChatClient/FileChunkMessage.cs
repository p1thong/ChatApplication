namespace WpfChatClient
{
    public class FileChunkMessage : ChatMessage
    {
        public string FileName { get; set; }
        public string Data { get; set; }
    }
}