using System.IO;
using System.Windows.Controls;

namespace WpfChatClient
{
    public class FileTransferContext
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public long BytesReceived { get; set; }
        public MemoryStream Buffer { get; set; } = new MemoryStream();

        public StackPanel FilePanel { get; set; }
        public ProgressBar Progress { get; set; }
        public Button SaveButton { get; set; }
    }

}