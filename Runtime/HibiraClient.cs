using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Hibira
{
    public enum HibiraConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
    }

    /// <summary>
    /// A Pusher Channels protocol (v7) client for Hibira and other
    /// Pusher-compatible servers.
    /// </summary>
    /// <remarks>
    /// Public channels need only the app key. Private and presence channels are
    /// signed by your own backend: set <see cref="HibiraOptions.AuthEndpoint"/>
    /// (the same endpoint pusher-js would use) or <see cref="HibiraOptions.Authorizer"/>.
    /// Events and callbacks are raised on Unity's main thread.
    /// <code>
    /// var hibira = new HibiraClient(new HibiraOptions {
    ///     AppKey = "your-app-key",
    ///     AuthEndpoint = "https://your-game.example/hibira/auth",
    /// });
    /// hibira.Connect();
    ///
    /// var room = hibira.Subscribe("presence-room-42");
    /// room.Bind("client-move", e => Debug.Log($"{e.UserId} moved {e.DataJson}"));
    /// room.Trigger("client-move", new Dictionary&lt;string, object&gt; { ["x"] = 3, ["y"] = 4 });
    /// </code>
    /// </remarks>
    public sealed class HibiraClient : IDisposable
    {
        public const string Version = "0.2.0";
        public const int Protocol = 7;

        /// <summary>The connection has changed state.</summary>
        public event Action<HibiraConnectionState> StateChanged;
        /// <summary>Connected, with the socket id the server gave this connection.</summary>
        public event Action<string> Connected;
        /// <summary>
        /// The connection closed: code, reason, and whether the client will
        /// reconnect (false after <see cref="Disconnect"/> or a close code the
        /// server says not to retry, 4000-4099).
        /// </summary>
        public event Action<int, string, bool> Disconnected;
        /// <summary>The server sent pusher:error, or the socket could not be opened.</summary>
        public event Action<int, string> Error;
        /// <summary>Any event on any channel (pusher_internal events excluded).</summary>
        public event Action<HibiraEvent> EventReceived;

        public HibiraOptions Options { get; }
        public HibiraConnectionState State { get; private set; } = HibiraConnectionState.Disconnected;
        /// <summary>This connection's socket id, null until connected.</summary>
        public string SocketId { get; private set; }

        /// <summary>Seconds on a monotonic clock. Tests replace it.</summary>
        internal Func<double> Clock = DefaultClock;

        static readonly Stopwatch Watch = Stopwatch.StartNew();
        static double DefaultClock() => Watch.Elapsed.TotalSeconds;

        readonly Dictionary<string, HibiraChannel> _channels = new Dictionary<string, HibiraChannel>();
        readonly ConcurrentQueue<Action> _posted = new ConcurrentQueue<Action>();
        readonly List<Timer> _timers = new List<Timer>();
        readonly Random _random = new Random();
        IHibiraTransport _transport;
        bool _wantConnection;
        int _attempt;
        double _retryAt = -1;
        double _lastActivity;
        double _pingSentAt = -1;
        double _serverTimeout;
        int _nextTimer;
        bool _disposed;

        public HibiraClient(string appKey) : this(new HibiraOptions { AppKey = appKey }) { }

        public HibiraClient(HibiraOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
#if UNITY_5_3_OR_NEWER
            if (!options.ManualTick) HibiraRunner.Register(this);
#endif
        }

        /// <summary>
        /// Open the connection. Channels you subscribed to are (re)subscribed once
        /// it is up.
        /// </summary>
        public void Connect()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HibiraClient));
            if (string.IsNullOrEmpty(Options.AppKey)) throw new InvalidOperationException("HibiraClient: AppKey is not set");
            _wantConnection = true;
            _retryAt = -1;
            if (_transport == null) Open();
        }

        /// <summary>
        /// Close the connection and stop reconnecting. Subscriptions are kept and
        /// come back on the next <see cref="Connect"/>.
        /// </summary>
        public void Disconnect()
        {
            _wantConnection = false;
            _retryAt = -1;
            if (_transport != null)
            {
                _transport.Close(1000, "client disconnect");
                Pump();
            }
            else
            {
                SetState(HibiraConnectionState.Disconnected);
            }
        }

        /// <summary>Subscribe to a channel, or return the existing subscription.</summary>
        public HibiraChannel Subscribe(string channelName)
        {
            if (string.IsNullOrEmpty(channelName)) throw new ArgumentException("channel name is empty", nameof(channelName));
            if (_channels.TryGetValue(channelName, out var existing)) return existing;
            var ch = new HibiraChannel(this, channelName);
            _channels[channelName] = ch;
            if (State == HibiraConnectionState.Connected) SendSubscribe(ch);
            return ch;
        }

        public void Unsubscribe(string channelName)
        {
            if (!_channels.TryGetValue(channelName, out var ch)) return;
            _channels.Remove(channelName);
            if (ch.IsSubscribed && State == HibiraConnectionState.Connected)
                Send(new Dictionary<string, object> { ["event"] = "pusher:unsubscribe", ["data"] = new Dictionary<string, object> { ["channel"] = channelName } });
            ch.Reset();
        }

        /// <summary>The subscription to <paramref name="channelName"/>, or null.</summary>
        public HibiraChannel Channel(string channelName) =>
            _channels.TryGetValue(channelName, out var ch) ? ch : null;

        public IReadOnlyCollection<HibiraChannel> Channels => _channels.Values;

        /// <summary>
        /// Send a client event (its name starts with "client-") on a private or
        /// presence channel. The app needs client events turned on in the Hibira
        /// portal. <paramref name="data"/> is anything <see cref="Json.Serialize"/>
        /// writes. Returns false when not connected.
        /// </summary>
        public bool Trigger(string channelName, string eventName, object data)
        {
            if (eventName == null || !eventName.StartsWith("client-", StringComparison.Ordinal))
                throw new ArgumentException($"client event names start with \"client-\": {eventName}", nameof(eventName));
            if (!IsPrivate(channelName))
                throw new ArgumentException($"client events need a private or presence channel: {channelName}", nameof(channelName));
            if (State != HibiraConnectionState.Connected) return false;
            return Send(new Dictionary<string, object> { ["event"] = eventName, ["channel"] = channelName, ["data"] = data });
        }

        /// <summary>
        /// Read the socket, fire events and timers. Runs every frame by itself
        /// unless <see cref="HibiraOptions.ManualTick"/> is set.
        /// </summary>
        public void Tick()
        {
            if (_disposed) return;
            var now = Clock();
            while (_posted.TryDequeue(out var action)) action();
            if (_retryAt >= 0 && now >= _retryAt)
            {
                _retryAt = -1;
                if (_wantConnection && _transport == null) Open();
            }
            Pump();
            if (State == HibiraConnectionState.Connected) CheckActivity(now);
            RunTimers(now);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _wantConnection = false;
            _retryAt = -1;
            if (_transport != null)
            {
                _transport.Close(1000, "client disconnect");
                Pump();
            }
            _disposed = true;
            _timers.Clear();
#if UNITY_5_3_OR_NEWER
            HibiraRunner.Unregister(this);
#endif
        }

        // ---- connection ----------------------------------------------------

        string Url()
        {
            var scheme = Options.UseTls ? "wss" : "ws";
            var defaultPort = Options.UseTls ? 443 : 80;
            var authority = Options.Port == defaultPort ? Options.Host : $"{Options.Host}:{Options.Port}";
            return $"{scheme}://{authority}/app/{Uri.EscapeDataString(Options.AppKey)}?protocol={Protocol}&client=unity&version={Version}";
        }

        void Open()
        {
            SetState(_attempt == 0 ? HibiraConnectionState.Connecting : HibiraConnectionState.Reconnecting);
            _transport = (Options.TransportFactory ?? HibiraTransports.CreateDefault)();
            _lastActivity = Clock();
            _pingSentAt = -1;
            try
            {
                _transport.Open(Url());
            }
            catch (Exception e)
            {
                Error?.Invoke(0, $"could not open {Url()}: {e.Message}");
                _transport.Close(1006, e.Message);
            }
        }

        void Pump()
        {
            var transport = _transport;
            while (transport != null && transport == _transport && transport.TryReceive(out var ev))
            {
                switch (ev.Kind)
                {
                    case TransportEventKind.Open:
                        _lastActivity = Clock();
                        break;
                    case TransportEventKind.Message:
                        _lastActivity = Clock();
                        _pingSentAt = -1;
                        HandleMessage(ev.Text);
                        break;
                    case TransportEventKind.Close:
                        _transport = null;
                        transport.Dispose();
                        Closed(ev.Code, ev.Text);
                        return;
                }
            }
        }

        void CheckActivity(double now)
        {
            var limit = Options.ActivityTimeout;
            if (_serverTimeout > 0) limit = Math.Min(limit, _serverTimeout);
            if (_pingSentAt >= 0)
            {
                if (now - _pingSentAt > Options.PongTimeout)
                {
                    _pingSentAt = -1;
                    _transport?.Close(4201, "pong timeout");
                    Pump();
                }
            }
            else if (now - _lastActivity > limit)
            {
                _pingSentAt = now;
                Send(new Dictionary<string, object> { ["event"] = "pusher:ping", ["data"] = new Dictionary<string, object>() });
            }
        }

        void Closed(int code, string reason)
        {
            SocketId = null;
            foreach (var ch in _channels.Values) ch.Reset();
            var permanent = code >= 4000 && code <= 4099;
            if (permanent) _wantConnection = false;
            var retry = _wantConnection && Options.AutoReconnect && !_disposed;
            Disconnected?.Invoke(code, reason, retry);
            if (retry) ScheduleReconnect(code);
            else SetState(HibiraConnectionState.Disconnected);
        }

        void ScheduleReconnect(int code)
        {
            SetState(HibiraConnectionState.Reconnecting);
            double delay;
            if (code >= 4200 && code <= 4299) delay = 0; // the server asks for an immediate reconnect
            else if (code >= 4100 && code <= 4199) delay = 1 + _attempt; // over capacity: back off
            else delay = Math.Min(30, Math.Pow(2, _attempt)) * (0.5 + _random.NextDouble() * 0.5);
            _attempt++;
            _retryAt = Clock() + delay;
        }

        void SetState(HibiraConnectionState s)
        {
            if (s == State) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        bool Send(Dictionary<string, object> message) =>
            _transport != null && _transport.Send(Json.Serialize(message));

        // ---- protocol ------------------------------------------------------

        void HandleMessage(string text)
        {
            if (!Json.TryParse(text, out var parsed) || !(parsed is Dictionary<string, object> msg)) return;
            if (!(Json.Get(msg, "event") is string eventName)) return;
            var rawData = Json.Get(msg, "data");
            var data = Decode(rawData, out var dataJson);
            var channelName = Json.Get(msg, "channel") as string;
            var userId = Json.Get(msg, "user_id") is object uid ? Json.ToText(uid) : null;
            switch (eventName)
            {
                case "pusher:connection_established":
                {
                    var d = Json.AsObject(data);
                    SocketId = Json.ToText(Json.Get(d, "socket_id"));
                    _serverTimeout = Json.ToDouble(Json.Get(d, "activity_timeout"));
                    _attempt = 0;
                    SetState(HibiraConnectionState.Connected);
                    Connected?.Invoke(SocketId);
                    foreach (var ch in new List<HibiraChannel>(_channels.Values)) SendSubscribe(ch);
                    return;
                }
                case "pusher:error":
                {
                    var d = Json.AsObject(data);
                    Error?.Invoke((int)Json.ToDouble(Json.Get(d, "code")), Json.ToText(Json.Get(d, "message")));
                    return;
                }
                case "pusher:ping":
                    Send(new Dictionary<string, object> { ["event"] = "pusher:pong", ["data"] = new Dictionary<string, object>() });
                    return;
                case "pusher:pong":
                    return;
            }
            var ev = new HibiraEvent(channelName, eventName, data, dataJson, userId);
            if (channelName != null && _channels.TryGetValue(channelName, out var channel)) channel.Handle(ev);
            if (!eventName.StartsWith("pusher_internal:", StringComparison.Ordinal)) EventReceived?.Invoke(ev);
        }

        /// <summary>
        /// Pusher sends most payloads as JSON inside a string; decode it when it
        /// is JSON, as pusher-js does, and keep plain strings as they are.
        /// </summary>
        static object Decode(object raw, out string json)
        {
            if (raw is string s)
            {
                if (Json.TryParse(s, out var parsed))
                {
                    json = s;
                    return parsed;
                }
                json = Json.Serialize(s);
                return s;
            }
            json = Json.Serialize(raw);
            return raw;
        }

        static bool IsPrivate(string channelName) =>
            channelName != null && (channelName.StartsWith("private-", StringComparison.Ordinal) || channelName.StartsWith("presence-", StringComparison.Ordinal));

        void SendSubscribe(HibiraChannel ch)
        {
            var name = ch.Name;
            if (name.StartsWith("private-encrypted-", StringComparison.Ordinal))
            {
                ch.Failed(0, "encrypted channels are not supported");
                return;
            }
            if (!IsPrivate(name))
            {
                Send(new Dictionary<string, object> { ["event"] = "pusher:subscribe", ["data"] = new Dictionary<string, object> { ["channel"] = name } });
                return;
            }
            var socketId = SocketId;
            Task<HibiraAuth> task;
            try
            {
                task = Authorize(socketId, name) ?? Task.FromResult<HibiraAuth>(null);
            }
            catch (Exception e)
            {
                task = Task.FromException<HibiraAuth>(e);
            }
            // The authorizer may finish on any thread; continue on the main one.
            task.ContinueWith(t => _posted.Enqueue(() => Authorized(ch, socketId, t)), TaskContinuationOptions.ExecuteSynchronously);
        }

        void Authorized(HibiraChannel ch, string socketId, Task<HibiraAuth> task)
        {
            // The connection may have dropped, or the channel been unsubscribed,
            // while the auth request was in flight.
            if (socketId != SocketId || Channel(ch.Name) != ch) return;
            if (task.IsFaulted || task.IsCanceled)
            {
                var e = task.Exception?.GetBaseException();
                ch.Failed(e is HibiraAuthException ae ? ae.Status : 0, e?.Message ?? "auth canceled");
                return;
            }
            var auth = task.Result;
            if (auth == null || string.IsNullOrEmpty(auth.auth))
            {
                ch.Failed(0, "auth refused");
                return;
            }
            var data = new Dictionary<string, object> { ["channel"] = ch.Name, ["auth"] = auth.auth };
            if (!string.IsNullOrEmpty(auth.channel_data))
            {
                data["channel_data"] = auth.channel_data;
                ch.SetChannelData(auth.channel_data);
            }
            Send(new Dictionary<string, object> { ["event"] = "pusher:subscribe", ["data"] = data });
        }

        Task<HibiraAuth> Authorize(string socketId, string channelName)
        {
            if (Options.Authorizer != null) return Options.Authorizer(socketId, channelName);
            if (string.IsNullOrEmpty(Options.AuthEndpoint))
                throw new HibiraAuthException(0, $"set AuthEndpoint or Authorizer to use {channelName}");
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("socket_id", socketId),
                new KeyValuePair<string, string>("channel_name", channelName),
            };
            foreach (var kv in Options.AuthParams)
                if (kv.Key != "socket_id" && kv.Key != "channel_name") form.Add(kv);
            return HibiraHttp.PostForm(Options.AuthEndpoint, Options.AuthHeaders, HibiraHttp.Form(form)).ContinueWith(t =>
            {
                var res = t.Result;
                Json.TryParse(res.Body, out var parsed);
                var body = parsed as Dictionary<string, object>;
                if (res.Status != 200 || body == null || !(Json.Get(body, "auth") is string authValue))
                {
                    var message = res.Error ?? (Json.Get(body, "error") is object err ? Json.ToText(err) : $"auth endpoint returned {res.Status}");
                    throw new HibiraAuthException(res.Status, message);
                }
                return new HibiraAuth(authValue, Json.Get(body, "channel_data") as string);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        // ---- timers (used by HibiraRoom) -----------------------------------

        struct Timer
        {
            public int Id;
            public double At;
            public Action Action;
        }

        /// <summary>Run <paramref name="action"/> on the main thread after <paramref name="seconds"/>.</summary>
        internal int After(double seconds, Action action)
        {
            var id = ++_nextTimer;
            _timers.Add(new Timer { Id = id, At = Clock() + seconds, Action = action });
            return id;
        }

        internal void Cancel(int timerId)
        {
            if (timerId == 0) return;
            _timers.RemoveAll(t => t.Id == timerId);
        }

        void RunTimers(double now)
        {
            if (_timers.Count == 0) return;
            List<Timer> due = null;
            for (var i = _timers.Count - 1; i >= 0; i--)
            {
                if (_timers[i].At > now) continue;
                (due ??= new List<Timer>()).Add(_timers[i]);
                _timers.RemoveAt(i);
            }
            if (due == null) return;
            due.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
            foreach (var t in due) t.Action();
        }
    }
}
