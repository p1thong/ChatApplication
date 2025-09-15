namespace ChatServer
{
    public class ChatSocketService : BackgroundService
    {
        private readonly ChatSocketServer _chatServer;

        public ChatSocketService(ChatSocketServer chatServer)
        {
            _chatServer = chatServer;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _chatServer.StartAsync();
        }
    }
}