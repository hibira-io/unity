using System;

namespace Hibira
{
    /// <summary>What happened on a transport, in the order it happened.</summary>
    public enum TransportEventKind
    {
        /// <summary>The WebSocket is open.</summary>
        Open,
        /// <summary>A text frame arrived; see <see cref="TransportEvent.Text"/>.</summary>
        Message,
        /// <summary>The socket closed (or never opened). Nothing follows it.</summary>
        Close,
    }

    public readonly struct TransportEvent
    {
        public readonly TransportEventKind Kind;
        /// <summary>The frame for Message, the reason for Close.</summary>
        public readonly string Text;
        /// <summary>The close code for Close.</summary>
        public readonly int Code;

        public TransportEvent(TransportEventKind kind, string text = null, int code = 0)
        {
            Kind = kind;
            Text = text;
            Code = code;
        }

        public static TransportEvent Opened() => new TransportEvent(TransportEventKind.Open);
        public static TransportEvent Received(string text) => new TransportEvent(TransportEventKind.Message, text);
        public static TransportEvent Closed(int code, string reason) => new TransportEvent(TransportEventKind.Close, reason ?? "", code);
    }

    /// <summary>
    /// One WebSocket connection. <see cref="HibiraClient"/> makes a new one for
    /// every connection attempt and polls it from the main thread, so an
    /// implementation may queue events from any thread.
    /// </summary>
    /// <remarks>
    /// The package picks <see cref="ClientWebSocketTransport"/> on native
    /// platforms and in the Editor, and the browser's WebSocket in WebGL builds.
    /// Set <see cref="HibiraOptions.TransportFactory"/> to use another one.
    /// </remarks>
    public interface IHibiraTransport : IDisposable
    {
        void Open(string url);
        /// <summary>Send a text frame; false when the socket is not open.</summary>
        bool Send(string text);
        /// <summary>
        /// Close the socket. A Close event with this code is queued right away;
        /// nothing is delivered after it.
        /// </summary>
        void Close(int code, string reason);
        /// <summary>The next event, if any. Called every frame on the main thread.</summary>
        bool TryReceive(out TransportEvent ev);
    }

    public static class HibiraTransports
    {
        /// <summary>The transport for the platform this build runs on.</summary>
        public static IHibiraTransport CreateDefault()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new WebGLWebSocketTransport();
#else
            return new ClientWebSocketTransport();
#endif
        }
    }
}
