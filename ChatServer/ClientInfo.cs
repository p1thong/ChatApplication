using System.Net.Sockets;

namespace ChatServer
{
    public class ClientInfo
    {
        public Socket Socket { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Id { get; set; } = Guid.NewGuid().ToString();
    }
}