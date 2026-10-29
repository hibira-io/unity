using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hibira
{
    /// <summary>
    /// Signs a private or presence subscription, like pusher-js's
    /// <c>channelAuthorization.customHandler</c>. Return null to refuse, or
    /// throw <see cref="HibiraAuthException"/> with the status your backend gave.
    /// </summary>
    public delegate Task<HibiraAuth> HibiraAuthorizer(string socketId, string channelName);

    /// <summary>What your auth endpoint returns: <c>{"auth": ..., "channel_data": ...}</c>.</summary>
    [Serializable]
    public class HibiraAuth
    {
        public string auth;
        /// <summary>Presence channels only: the member's user_id and user_info, as JSON.</summary>
        public string channel_data;

        public HibiraAuth() { }

        public HibiraAuth(string auth, string channelData = null)
        {
            this.auth = auth;
            channel_data = channelData;
        }
    }

    /// <summary>The auth endpoint refused a subscription, or could not be reached.</summary>
    public class HibiraAuthException : Exception
    {
        /// <summary>The HTTP status (403 for a full room, say), or 0 without one.</summary>
        public int Status { get; }

        public HibiraAuthException(int status, string message) : base(message) => Status = status;
    }

    public class HibiraOptions
    {
        /// <summary>The app key from the Hibira portal. Never put the app secret in a game.</summary>
        public string AppKey = "";
        /// <summary>WebSocket host. Hibira: ws.hibira.io.</summary>
        public string Host = "ws.hibira.io";
        public int Port = 443;
        public bool UseTls = true;

        /// <summary>
        /// URL that signs private and presence subscriptions: it gets a form POST
        /// with socket_id and channel_name (plus <see cref="AuthParams"/>) and
        /// returns <c>{"auth": ..., "channel_data": ...}</c>, as for pusher-js.
        /// </summary>
        public string AuthEndpoint = "";
        /// <summary>Extra headers for the auth request, e.g. Authorization.</summary>
        public Dictionary<string, string> AuthHeaders = new Dictionary<string, string>();
        /// <summary>Extra form fields for the auth request.</summary>
        public Dictionary<string, string> AuthParams = new Dictionary<string, string>();
        /// <summary>Sign subscriptions yourself instead of <see cref="AuthEndpoint"/>.</summary>
        public HibiraAuthorizer Authorizer;

        /// <summary>Reconnect after a dropped connection, with backoff.</summary>
        public bool AutoReconnect = true;
        /// <summary>
        /// Seconds without traffic before the client pings the server. The
        /// server's own activity_timeout wins when it is shorter.
        /// </summary>
        public double ActivityTimeout = 120;
        /// <summary>Seconds to wait for any reply to a ping before reconnecting.</summary>
        public double PongTimeout = 30;

        /// <summary>
        /// Makes the WebSocket for each connection attempt. Defaults to the
        /// browser's WebSocket in WebGL builds and ClientWebSocket elsewhere.
        /// </summary>
        public Func<IHibiraTransport> TransportFactory;

        /// <summary>
        /// When false (the default) the client runs itself on Unity's main
        /// thread every frame. Set it to true to call <see cref="HibiraClient.Tick"/>
        /// yourself.
        /// </summary>
        public bool ManualTick;
    }
}
