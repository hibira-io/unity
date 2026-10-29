using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hibira
{
    /// <summary>
    /// A transport on <see cref="ClientWebSocket"/>, for the Editor, desktop,
    /// Android and iOS. Not available in WebGL builds, which have no threads or
    /// sockets; those use the browser's WebSocket instead.
    /// </summary>
    public sealed class ClientWebSocketTransport : IHibiraTransport
    {
        readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly object _sendLock = new object();
        ClientWebSocket _ws;
        Task _sending = Task.CompletedTask;
        int _closed;

        public void Open(string url)
        {
            if (_ws != null) throw new InvalidOperationException("ClientWebSocketTransport: Open() was already called");
            _ws = new ClientWebSocket();
            _ = Run(new Uri(url));
        }

        async Task Run(Uri uri)
        {
            try
            {
                await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _closed) != 0) return;
                _events.Enqueue(TransportEvent.Opened());
                var buffer = new byte[8192];
                var message = new MemoryStream();
                while (true)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var code = (int?)_ws.CloseStatus ?? 1005;
                        var reason = _ws.CloseStatusDescription;
                        try
                        {
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The server is gone already; nothing to answer.
                        }
                        Finish(code, reason);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    if (result.MessageType == WebSocketMessageType.Text && Volatile.Read(ref _closed) == 0)
                        _events.Enqueue(TransportEvent.Received(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)));
                    message.SetLength(0);
                }
            }
            catch (Exception e)
            {
                Finish(1006, e.Message);
            }
        }

        public bool Send(string text)
        {
            if (_ws == null || Volatile.Read(ref _closed) != 0 || _ws.State != WebSocketState.Open) return false;
            var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(text));
            var ws = _ws;
            var token = _cts.Token;
            // ClientWebSocket allows one send at a time, so sends are chained.
            lock (_sendLock)
            {
                _sending = _sending.ContinueWith(
                    _ => ws.SendAsync(bytes, WebSocketMessageType.Text, true, token),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
            return true;
        }

        public void Close(int code, string reason)
        {
            if (!Finish(code, reason)) return;
            _ = CloseQuietly(code, reason);
        }

        async Task CloseQuietly(int code, string reason)
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    using (var timeout = new CancellationTokenSource(2000))
                        await _ws.CloseOutputAsync((WebSocketCloseStatus)code, reason ?? "", timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Closing is best effort.
            }
            finally
            {
                _cts.Cancel();
            }
        }

        bool Finish(int code, string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return false;
            _events.Enqueue(TransportEvent.Closed(code, reason));
            return true;
        }

        public bool TryReceive(out TransportEvent ev) => _events.TryDequeue(out ev);

        public void Dispose()
        {
            if (Volatile.Read(ref _closed) == 0) Close(1000, "");
            else _cts.Cancel();
        }
    }
}
