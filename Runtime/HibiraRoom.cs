using System;
using System.Collections.Generic;
using System.Linq;

namespace Hibira
{
    /// <summary>A member of a <see cref="HibiraRoom"/>.</summary>
    public sealed class HibiraPlayer
    {
        public string Id { get; }
        /// <summary>The user_info your auth endpoint signed (usually a Dictionary&lt;string, object&gt;).</summary>
        public object Info { get; }
        public bool Ready { get; }
        public bool Host { get; }
        /// <summary>This is you.</summary>
        public bool Me { get; }

        public HibiraPlayer(string id, object info, bool ready, bool host, bool me)
        {
            Id = id;
            Info = info;
            Ready = ready;
            Host = host;
            Me = me;
        }

        /// <summary>user_info's "name", or the id.</summary>
        public string Name => Json.Get(Info as Dictionary<string, object>, "name") is string n ? n : Id;

        /// <summary>A field of user_info, or null.</summary>
        public object this[string key] => Json.Get(Info as Dictionary<string, object>, key);

        public override string ToString() => $"{Name} ({Id}){(Host ? " host" : "")}{(Ready ? " ready" : "")}";
    }

    /// <summary>A room chat message.</summary>
    public sealed class HibiraChatMessage
    {
        public string Id { get; }
        /// <summary>The sender's user id.</summary>
        public string From { get; }
        /// <summary>The sender's user_info.</summary>
        public object Info { get; }
        public string Text { get; }
        /// <summary>Whatever the sender passed as extra, or null.</summary>
        public object Extra { get; }
        /// <summary>When it arrived, in Unix milliseconds.</summary>
        public long At { get; }
        /// <summary>You sent it.</summary>
        public bool Mine { get; }

        public HibiraChatMessage(string id, string from, object info, string text, object extra, long at, bool mine)
        {
            Id = id;
            From = from;
            Info = info;
            Text = text;
            Extra = extra;
            At = at;
            Mine = mine;
        }

        /// <summary>The sender's user_info "name", or their id.</summary>
        public string FromName => Json.Get(Info as Dictionary<string, object>, "name") is string n ? n : From;
    }

    /// <summary>Options for <see cref="HibiraRoom.Join"/>.</summary>
    public class HibiraRoomOptions
    {
        /// <summary>Seats; members past it are told the room is full.</summary>
        public int Capacity = int.MaxValue;
        /// <summary>Leave by yourself on <see cref="HibiraRoom.Full"/>.</summary>
        public bool LeaveWhenFull = true;
        /// <summary>Chat messages kept in <see cref="HibiraRoom.Messages"/>.</summary>
        public int MaxMessages = 100;
        /// <summary>Seconds a typing signal lasts.</summary>
        public double TypingTimeout = 4;
        /// <summary>Seconds the host waits before sending its snapshot to a new member.</summary>
        public double StateDelay = 0.15;
    }

    /// <summary>
    /// Game rooms on presence channels: seats, a host, ready flags, settings,
    /// kick, start, and chat with typing indicators.
    /// </summary>
    /// <remarks>
    /// A C# port of <a href="https://github.com/hibira-io/lobby-js">@hibira-io/lobby</a>
    /// that speaks the same client-lobby-* events, so Unity players, Godot
    /// players and browser players can share a room. Nothing runs on a server
    /// besides the auth endpoint: members come from the presence channel,
    /// everything else is a client event, and every member orders the room by
    /// the joined_at the auth endpoint signs into user_info (then by user id).
    /// The first member is the host; members past the capacity are told the
    /// room is full.
    /// <code>
    /// var room = HibiraRoom.Join(hibira, "presence-room-42", new HibiraRoomOptions { Capacity = 4 });
    /// room.Joined += (players, host) => Render(players);
    /// room.MembersChanged += (players, joined, left) => Render(players);
    /// room.MessageReceived += m => AddChatLine(m.FromName, m.Text);
    /// room.Started += (data, by) => StartGame(data);
    /// room.SetReady(true);
    /// if (room.IsHost) room.Start(new Dictionary&lt;string, object&gt; { ["seed"] = 1234 });
    /// </code>
    /// </remarks>
    public sealed class HibiraRoom
    {
        public const string EventState = "client-lobby-state";
        public const string EventReady = "client-lobby-ready";
        public const string EventSettings = "client-lobby-settings";
        public const string EventKick = "client-lobby-kick";
        public const string EventStart = "client-lobby-start";
        public const string EventChat = "client-lobby-chat";
        public const string EventTyping = "client-lobby-typing";

        /// <summary>Subscribed: the players in seat order, and the host.</summary>
        public event Action<IReadOnlyList<HibiraPlayer>, HibiraPlayer> Joined;
        /// <summary>
        /// Players changed (a join, a leave, a ready flag): the players, and who
        /// joined or left (null when neither).
        /// </summary>
        public event Action<IReadOnlyList<HibiraPlayer>, HibiraPlayer, HibiraPlayer> MembersChanged;
        /// <summary>A new host (the old one left): the host and the previous host's id.</summary>
        public event Action<HibiraPlayer, string> HostChanged;
        /// <summary>The host changed the settings: the settings and who changed them.</summary>
        public event Action<IReadOnlyDictionary<string, object>, string> SettingsChanged;
        /// <summary>The host started the game: what they passed to <see cref="Start"/>, and the host's id.</summary>
        public event Action<object, string> Started;
        public event Action<HibiraChatMessage> MessageReceived;
        /// <summary>The ids of the members typing now (you excluded).</summary>
        public event Action<IReadOnlyList<string>> TypingChanged;
        /// <summary>The host asked you to leave (who, and the reason); the room has already left.</summary>
        public event Action<string, object> Kicked;
        /// <summary>You are past capacity (two players joined at the same moment).</summary>
        public event Action<int> Full;
        /// <summary>The auth endpoint refused the subscription (403 when the room is full).</summary>
        public event Action<int, string> JoinFailed;

        public HibiraChannel Channel { get; }
        public string Name => Channel.Name;
        public int Capacity { get; }
        public bool LeaveWhenFull { get; }
        public int MaxMessages { get; }
        public double TypingTimeout { get; }
        public double StateDelay { get; }
        /// <summary>True once subscribed.</summary>
        public bool IsJoined { get; private set; }
        /// <summary>"waiting" until the host calls <see cref="Start"/>, then "started".</summary>
        public string Phase { get; private set; } = "waiting";
        /// <summary>Room settings; only the host changes them.</summary>
        public IReadOnlyDictionary<string, object> Settings => _settings;
        /// <summary>Chat messages received since joining, oldest first.</summary>
        public IReadOnlyList<HibiraChatMessage> Messages => _messages;

        readonly WeakReference<HibiraClient> _client;
        Dictionary<string, object> _settings = new Dictionary<string, object>();
        readonly List<HibiraChatMessage> _messages = new List<HibiraChatMessage>();
        readonly Dictionary<string, bool> _ready = new Dictionary<string, bool>();
        readonly Dictionary<string, int> _typing = new Dictionary<string, int>(); // id -> timer
        readonly Action<object> _onSubscribed;
        readonly Action<int, string> _onSubscriptionError;
        readonly Action<string, object> _onMemberAdded;
        readonly Action<string, object> _onMemberRemoved;
        readonly Action<HibiraEvent> _onEvent;
        string _host;
        bool _full;
        long _seq;
        int _stateTimer;
        double _lastTyping = double.NegativeInfinity;
        bool _ownsSubscription;
        bool _left;

        /// <summary>Subscribe to <paramref name="channelName"/> (a presence- channel) and track it as a room.</summary>
        public static HibiraRoom Join(HibiraClient client, string channelName, HibiraRoomOptions options = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (channelName == null || !channelName.StartsWith("presence-", StringComparison.Ordinal) || channelName.StartsWith("presence-large-", StringComparison.Ordinal))
                throw new ArgumentException($"HibiraRoom: {channelName} is not a presence channel (presence-large- channels do not allow client events)", nameof(channelName));
            var room = new HibiraRoom(client.Subscribe(channelName), options);
            room._ownsSubscription = true;
            return room;
        }

        /// <summary>
        /// Track a channel you subscribed to yourself. Create it right after
        /// subscribing so no event is missed.
        /// </summary>
        public HibiraRoom(HibiraChannel channel, HibiraRoomOptions options = null)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            var client = channel.Client ?? throw new InvalidOperationException("HibiraRoom: the channel's client is gone");
            _client = new WeakReference<HibiraClient>(client);
            options ??= new HibiraRoomOptions();
            Capacity = Math.Max(1, options.Capacity);
            LeaveWhenFull = options.LeaveWhenFull;
            MaxMessages = Math.Max(1, options.MaxMessages);
            TypingTimeout = options.TypingTimeout;
            StateDelay = options.StateDelay;
            _onSubscribed = _ => OnSubscribed();
            _onSubscriptionError = (status, message) => JoinFailed?.Invoke(status, message);
            _onMemberAdded = (id, _) => OnMemberAdded(id);
            _onMemberRemoved = OnMemberRemoved;
            _onEvent = OnEvent;
            channel.Subscribed += _onSubscribed;
            channel.SubscriptionError += _onSubscriptionError;
            channel.MemberAdded += _onMemberAdded;
            channel.MemberRemoved += _onMemberRemoved;
            channel.EventReceived += _onEvent;
            if (channel.IsSubscribed && channel.Members.Count > 0) OnSubscribed();
        }

        // ---- reading the room ------------------------------------------------

        /// <summary>This connection's member id (null until joined).</summary>
        public string MyId => Channel.MyId;

        /// <summary>Seated members in room order.</summary>
        public IReadOnlyList<HibiraPlayer> Players => Ordered().Take(Capacity).Select(Member).ToList();

        /// <summary>Members past capacity (normally empty, briefly not after a join race).</summary>
        public IReadOnlyList<HibiraPlayer> Waiting => Ordered().Skip(Capacity).Select(Member).ToList();

        /// <summary>The host, or null before joining.</summary>
        public HibiraPlayer Host => _host == null ? null : Member(_host);

        public bool IsHost => MyId != null && _host == MyId;

        /// <summary>You, or null before joining.</summary>
        public HibiraPlayer Me => MyId != null && Channel.Members.ContainsKey(MyId) ? Member(MyId) : null;

        /// <summary>True when there is at least one player and every player is ready.</summary>
        public bool AllReady
        {
            get
            {
                var p = Players;
                return p.Count > 0 && p.All(m => m.Ready);
            }
        }

        /// <summary>Ids of the members typing now (you excluded).</summary>
        public IReadOnlyList<string> Typing => _typing.Keys.ToList();

        HibiraPlayer Member(string id)
        {
            Channel.Members.TryGetValue(id, out var info);
            return new HibiraPlayer(id, info, _ready.TryGetValue(id, out var r) && r, id == _host, id == MyId);
        }

        List<string> Ordered()
        {
            var ids = Channel.Members.Keys.ToList();
            ids.Sort((a, b) =>
            {
                var ta = JoinedAt(Channel.Members[a]);
                var tb = JoinedAt(Channel.Members[b]);
                if (ta != tb) return ta.CompareTo(tb);
                return string.CompareOrdinal(a, b);
            });
            return ids;
        }

        // ---- actions ---------------------------------------------------------

        /// <summary>Mark yourself ready or not ready.</summary>
        public void SetReady(bool ready = true)
        {
            RequireJoined();
            _ready[MyId] = ready;
            TriggerEvent(EventReady, new Dictionary<string, object> { ["ready"] = ready });
            EmitMembers(null, null);
        }

        /// <summary>Host only: merge <paramref name="patch"/> into the room settings.</summary>
        public void SetSettings(IDictionary<string, object> patch)
        {
            RequireHost(nameof(SetSettings));
            var next = new Dictionary<string, object>(_settings);
            foreach (var kv in patch) next[kv.Key] = kv.Value;
            _settings = next;
            TriggerEvent(EventSettings, new Dictionary<string, object> { ["settings"] = _settings, ["seq"] = ++_seq });
            SettingsChanged?.Invoke(_settings, MyId);
        }

        /// <summary>
        /// Host only: ask a member to leave. The kicked client leaves by itself;
        /// to keep someone out for real, refuse them in your auth endpoint and
        /// terminate their connections with the HTTP API.
        /// </summary>
        public void Kick(string userId, object reason = null)
        {
            RequireHost(nameof(Kick));
            if (userId == MyId) throw new InvalidOperationException("HibiraRoom: the host cannot kick themselves");
            TriggerEvent(EventKick, new Dictionary<string, object> { ["user_id"] = userId, ["reason"] = reason });
        }

        /// <summary>
        /// Host only: start the game. <paramref name="data"/> reaches every
        /// member's <see cref="Started"/> (yours too), e.g. a seed. Returns false,
        /// and sends nothing, when <paramref name="requireAllReady"/> is set and
        /// someone is not ready.
        /// </summary>
        public bool Start(object data = null, bool requireAllReady = false)
        {
            RequireHost(nameof(Start));
            if (requireAllReady && !AllReady) return false;
            Phase = "started";
            TriggerEvent(EventStart, new Dictionary<string, object> { ["data"] = data, ["seq"] = ++_seq });
            Started?.Invoke(data, MyId);
            return true;
        }

        /// <summary>Send a chat message to the room; returns it as stored in <see cref="Messages"/>.</summary>
        public HibiraChatMessage Send(string text, object extra = null)
        {
            RequireJoined();
            var msg = new Dictionary<string, object> { ["id"] = Guid.NewGuid().ToString(), ["text"] = text ?? "" };
            if (extra != null) msg["extra"] = extra;
            TriggerEvent(EventChat, msg);
            ClearTyping(MyId);
            return PushMessage(msg, MyId);
        }

        /// <summary>
        /// Tell the room you are typing. Call it on every keystroke; it sends at
        /// most once every TypingTimeout / 2 seconds.
        /// </summary>
        public void SendTyping()
        {
            RequireJoined();
            var now = Now();
            if (now - _lastTyping < TypingTimeout / 2) return;
            _lastTyping = now;
            TriggerEvent(EventTyping, new Dictionary<string, object>());
        }

        /// <summary>Stop tracking, and unsubscribe when <see cref="Join"/> made the subscription.</summary>
        public void Leave()
        {
            if (_left) return;
            _left = true;
            Channel.Subscribed -= _onSubscribed;
            Channel.SubscriptionError -= _onSubscriptionError;
            Channel.MemberAdded -= _onMemberAdded;
            Channel.MemberRemoved -= _onMemberRemoved;
            Channel.EventReceived -= _onEvent;
            var client = Client;
            foreach (var timer in _typing.Values) client?.Cancel(timer);
            _typing.Clear();
            client?.Cancel(_stateTimer);
            _stateTimer = 0;
            if (_ownsSubscription) Channel.Unsubscribe();
        }

        HibiraClient Client => _client.TryGetTarget(out var c) ? c : null;

        void RequireJoined()
        {
            if (!IsJoined || _left) throw new InvalidOperationException("HibiraRoom: not joined yet");
        }

        void RequireHost(string what)
        {
            RequireJoined();
            if (!IsHost) throw new InvalidOperationException($"HibiraRoom: only the host can {what}()");
        }

        void TriggerEvent(string eventName, Dictionary<string, object> data)
        {
            if (!Channel.Trigger(eventName, data))
            {
#if UNITY_5_3_OR_NEWER
                UnityEngine.Debug.LogWarning($"HibiraRoom: could not send {eventName} (not connected?)");
#endif
            }
        }

        // ---- presence --------------------------------------------------------

        void OnSubscribed()
        {
            IsJoined = true;
            Recompute();
            Joined?.Invoke(Players, Host);
            CheckFull();
        }

        void OnMemberAdded(string id)
        {
            Recompute();
            EmitMembers(Member(id), null);
            if (IsHost) ScheduleState();
            // Two members authorized at nearly the same time can both pass a
            // capacity check; the one that joined later is the one past capacity.
            CheckFull();
        }

        void OnMemberRemoved(string id, object info)
        {
            var gone = new HibiraPlayer(id, info, _ready.TryGetValue(id, out var r) && r, id == _host, false);
            _ready.Remove(id);
            var wasTyping = ClearTyping(id);
            Recompute();
            EmitMembers(null, gone);
            if (wasTyping) TypingChanged?.Invoke(Typing);
        }

        void Recompute()
        {
            var prev = _host;
            var order = Ordered();
            _host = order.Count > 0 ? order[0] : null;
            if (prev != null && prev != _host) HostChanged?.Invoke(Host, prev);
        }

        void CheckFull()
        {
            if (_full || MyId == null) return;
            if (Ordered().IndexOf(MyId) >= Capacity)
            {
                _full = true;
                Full?.Invoke(Capacity);
                if (LeaveWhenFull) Leave();
            }
        }

        void EmitMembers(HibiraPlayer joined, HibiraPlayer left) => MembersChanged?.Invoke(Players, joined, left);

        void ScheduleState()
        {
            var client = Client;
            if (client == null) return;
            client.Cancel(_stateTimer);
            _stateTimer = client.After(StateDelay, () =>
            {
                _stateTimer = 0;
                if (_left || !IsHost) return;
                var ready = new Dictionary<string, object>();
                foreach (var kv in _ready) ready[kv.Key] = kv.Value;
                TriggerEvent(EventState, new Dictionary<string, object>
                {
                    ["settings"] = _settings,
                    ["ready"] = ready,
                    ["phase"] = Phase,
                    ["seq"] = _seq,
                });
            });
        }

        // ---- client events ---------------------------------------------------

        bool FromHost(HibiraEvent ev) => ev.UserId != null && ev.UserId == _host;

        void OnEvent(HibiraEvent ev)
        {
            if (_left) return;
            var d = ev.Data as Dictionary<string, object> ?? new Dictionary<string, object>();
            switch (ev.Name)
            {
                case EventState:
                {
                    if (!FromHost(ev)) return;
                    var seq = Json.Get(d, "seq");
                    if (Json.IsNumber(seq) && Json.ToDouble(seq) < _seq) return;
                    if (Json.IsNumber(seq)) _seq = (long)Json.ToDouble(seq);
                    _settings = Json.Get(d, "settings") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    if (Json.Get(d, "phase") is string phase) Phase = phase;
                    if (Json.Get(d, "ready") is Dictionary<string, object> flags)
                    {
                        foreach (var kv in flags)
                            if (kv.Key != MyId && Channel.Members.ContainsKey(kv.Key)) _ready[kv.Key] = kv.Value is bool b && b;
                    }
                    SettingsChanged?.Invoke(_settings, ev.UserId);
                    EmitMembers(null, null);
                    return;
                }
                case EventReady:
                    if (ev.UserId == null || !Channel.Members.ContainsKey(ev.UserId)) return;
                    _ready[ev.UserId] = Json.Get(d, "ready") is bool ready && ready;
                    EmitMembers(null, null);
                    return;
                case EventSettings:
                    if (!FromHost(ev)) return;
                    _seq = Math.Max(_seq, (long)Json.ToDouble(Json.Get(d, "seq")));
                    _settings = Json.Get(d, "settings") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    SettingsChanged?.Invoke(_settings, ev.UserId);
                    return;
                case EventKick:
                    if (!FromHost(ev) || !(Json.Get(d, "user_id") is object target) || Json.ToText(target) != MyId) return;
                    Leave();
                    Kicked?.Invoke(ev.UserId, Json.Get(d, "reason"));
                    return;
                case EventStart:
                    if (!FromHost(ev)) return;
                    _seq = Math.Max(_seq, (long)Json.ToDouble(Json.Get(d, "seq")));
                    Phase = "started";
                    Started?.Invoke(Json.Get(d, "data"), ev.UserId);
                    return;
                case EventChat:
                {
                    if (ev.UserId == null || !(Json.Get(d, "text") is string)) return;
                    var wasTyping = ClearTyping(ev.UserId);
                    PushMessage(d, ev.UserId);
                    if (wasTyping) TypingChanged?.Invoke(Typing);
                    return;
                }
                case EventTyping:
                    OnTyping(ev.UserId);
                    return;
            }
        }

        void OnTyping(string id)
        {
            var client = Client;
            if (client == null || id == null || id == MyId || !Channel.Members.ContainsKey(id)) return;
            var had = _typing.TryGetValue(id, out var old);
            client.Cancel(old);
            _typing[id] = client.After(TypingTimeout, () =>
            {
                _typing.Remove(id);
                TypingChanged?.Invoke(Typing);
            });
            if (!had) TypingChanged?.Invoke(Typing);
        }

        bool ClearTyping(string id)
        {
            if (id == null || !_typing.TryGetValue(id, out var timer)) return false;
            Client?.Cancel(timer);
            _typing.Remove(id);
            return true;
        }

        HibiraChatMessage PushMessage(Dictionary<string, object> d, string from)
        {
            Channel.Members.TryGetValue(from, out var info);
            var msg = new HibiraChatMessage(
                Json.Get(d, "id") is string id ? id : Guid.NewGuid().ToString(),
                from,
                info,
                (string)d["text"],
                Json.Get(d, "extra"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                from == MyId);
            _messages.Add(msg);
            if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
            MessageReceived?.Invoke(msg);
            return msg;
        }

        double Now() => Client?.Clock() ?? 0;

        static double JoinedAt(object info) =>
            Json.Get(info as Dictionary<string, object>, "joined_at") is object t && Json.IsNumber(t) ? Json.ToDouble(t) : double.MaxValue;
    }
}
