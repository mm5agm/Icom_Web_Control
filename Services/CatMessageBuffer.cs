using System.Text;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Buffers incoming CAT serial data and splits it into complete messages
    /// </summary>
    public class CatMessageBuffer
    {
        private readonly StringBuilder _buffer = new();
        // StringBuilder isn't thread-safe. Since v2.4.2-pre2, AppendData is
        // called both from SerialPort.DataReceived (its own background thread)
        // and from CatMultiplexerService's per-command pending-bytes drain
        // (the command-queue processing thread) — two concurrent writers
        // where there used to be only one. Unsynchronized concurrent
        // Append/Remove/ToString on the same StringBuilder can corrupt its
        // internal state, silently dropping or garbling bytes (a lost
        // semicolon means a message is never extracted) rather than throwing.
        private readonly object _bufferLock = new();

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

            List<string> messages;
            lock (_bufferLock)
            {
                _buffer.Append(data);
                messages = ExtractCompleteMessages();
            }

            // Dispatch outside the lock so downstream handlers can't deadlock
            // against another AppendData call.
            foreach (var message in messages)
            {
                OnMessageReceived(message);
            }
        }

        /// <summary>
        /// Extract complete messages (ending with semicolon). Caller must hold _bufferLock.
        /// </summary>
        private List<string> ExtractCompleteMessages()
        {
            var messages = new List<string>();

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

                // Minimum: "XX;"
                if (message.Length >= 3)
                {
                    messages.Add(message);
                }
            }

            return messages;
        }

        private void OnMessageReceived(string message)
        {
            MessageReceived?.Invoke(this, new CatMessageReceivedEventArgs(message));
        }

        public void Clear()
        {
            lock (_bufferLock)
            {
                _buffer.Clear();
            }
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