#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace Hibira
{
    /// <summary>
    /// A transport on the browser's WebSocket, for WebGL builds, through
    /// Plugins/WebGL/HibiraWebSocket.jslib. The page queues what happens and
    /// the client reads it once a frame, so no callbacks cross into C#.
    /// </summary>
    public sealed class WebGLWebSocketTransport : IHibiraTransport
    {
        [DllImport("__Internal")] static extern int HibiraWS_Open(string url);
        [DllImport("__Internal")] static extern int HibiraWS_Send(int id, string text);
        [DllImport("__Internal")] static extern void HibiraWS_Close(int id, int code, string reason);
        [DllImport("__Internal")] static extern string HibiraWS_Poll(int id);
        [DllImport("__Internal")] static extern void HibiraWS_Free(int id);

        int _id;

        public void Open(string url)
        {
            if (_id != 0) throw new InvalidOperationException("WebGLWebSocketTransport: Open() was already called");
            _id = HibiraWS_Open(url);
        }

        public bool Send(string text) => _id != 0 && HibiraWS_Send(_id, text) == 1;

        public void Close(int code, string reason)
        {
            if (_id != 0) HibiraWS_Close(_id, code, reason ?? "");
        }

        public bool TryReceive(out TransportEvent ev)
        {
            ev = default;
            if (_id == 0) return false;
            var raw = HibiraWS_Poll(_id);
            if (raw == null) return false;
            ev = Decode(raw);
            return true;
        }

        // "o" open, "m<text>" message, "c<code>|<reason>" close.
        internal static TransportEvent Decode(string raw)
        {
            if (raw.Length == 0) return TransportEvent.Closed(1006, "bad event");
            switch (raw[0])
            {
                case 'o':
                    return TransportEvent.Opened();
                case 'm':
                    return TransportEvent.Received(raw.Substring(1));
                default:
                    var bar = raw.IndexOf('|');
                    var codeText = bar < 0 ? raw.Substring(1) : raw.Substring(1, bar - 1);
                    int.TryParse(codeText, out var code);
                    return TransportEvent.Closed(code == 0 ? 1006 : code, bar < 0 ? "" : raw.Substring(bar + 1));
            }
        }

        public void Dispose()
        {
            if (_id == 0) return;
            HibiraWS_Free(_id);
            _id = 0;
        }
    }
}
#endif
