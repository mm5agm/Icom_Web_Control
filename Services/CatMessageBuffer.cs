using System.Text;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Buffers incoming CAT serial data and splits it into complete messages
    /// </summary>
    public class CatMessageBuffer
    {
        private readonly StringBuilder _buffer = new();

        public event EventHandler<CatMessageReceivedEventArgs>? MessageReceived;

        public CatMessageBuffer(ILogger<CatMessageBuffer> logger)
        {
        }

        /// <summary>
        /// Append incoming data and process complete messages
        /// </summary>
        public void AppendData(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            _buffer.Append(data);
            ProcessMessages();
        }

        /// <summary>
        /// Extract and dispatch complete messages (ending with semicolon)
        /// </summary>
        private void ProcessMessages()
        {
            while (true)
            {
                string bufferContent = _buffer.ToString();
                int semicolonIndex = bufferContent.IndexOf(';');

                if (semicolonIndex == -1)
                {
                    // No complete message yet
                    break;
                }

                // Extract complete message including semicolon
                string message = bufferContent.Substring(0, semicolonIndex + 1);
                _buffer.Remove(0, semicolonIndex + 1);

                // Dispatch message
                if (message.Length >= 3) // Minimum: "XX;"
                {
                    // Debug logging removed as part of cleanup
                    OnMessageReceived(message);
                }
            }
        }

        private void OnMessageReceived(string message)
        {
            MessageReceived?.Invoke(this, new CatMessageReceivedEventArgs(message));
        }

        public void Clear()
        {
            _buffer.Clear();
        }

        public async Task<string> WaitForResponseAsync(string prefix, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<string>();
            void Handler(object? sender, CatMessageReceivedEventArgs e)
            {
                if (e.Message.StartsWith(prefix))
                {
                    tcs.TrySetResult(e.Message);
                }
            }

            MessageReceived += Handler;
            try
            {
                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                MessageReceived -= Handler;
            }
        }
    }

    public class CatMessageReceivedEventArgs : EventArgs
    {
        public string Message { get; }
        public string Command => Message.Length >= 2 ? Message.Substring(0, 2) : string.Empty;
        public DateTime Timestamp { get; }

        public CatMessageReceivedEventArgs(string message)
        {
            Message = message;
            Timestamp = DateTime.UtcNow;
        }
    }
}