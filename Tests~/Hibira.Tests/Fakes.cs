using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    /// <summary>A transport the test drives by hand, optionally wired to a <see cref="FakeServer"/>.</summary>
    sealed class FakeTransport : IHibiraTransport
    {
        public readonly Queue<TransportEvent> Incoming = new Queue<TransportEvent>();
        public readonly List<string> Sent = new List<string>();
        public FakeServer Server;
        public string Url;
        public bool IsClosed;
        public int CloseCode;
        public bool Disposed;

        public void Open(string url)
        {
            Url = url;
            Server?.Accept(this);
        }

        public bool Send(string text)
        {
            if (IsClosed) return false;
            Sent.Add(text);
            Server?.Receive(this, text);
            return true;
        }

        public void Close(int code, string reason)
        {
            if (IsClosed) return;
            IsClosed = true;
            CloseCode = code;
            Incoming.Enqueue(TransportEvent.Closed(code, reason));
            Server?.Drop(this);
        }

        public bool TryReceive(out TransportEvent ev)
        {
            if (Incoming.Count == 0)
            {
                ev = default;
                return false;
            }
            ev = Incoming.Dequeue();
            return true;
        }

        public void Dispose() => Disposed = true;

        // ---- test helpers ----

        public void Push(string json) => Incoming.Enqueue(TransportEvent.Received(json));

        public void Push(Dictionary<string, object> message) => Push(Json.Serialize(message));

        /// <summary>The server closes the socket.</summary>
        public void ServerClose(int code, string reason = "")
        {
            if (IsClosed) return;
            IsClosed = true;
            CloseCode = code;
            Incoming.Enqueue(TransportEvent.Closed(code, reason));
            Server?.Drop(this);
        }

        public List<Dictionary<string, object>> SentMessages() =>
            Sent.Select(s => (Dictionary<string, object>)Json.Parse(s)).ToList();

        public List<Dictionary<string, object>> SentEvents(string name) =>
            SentMessages().Where(m => (string)m["event"] == name).ToList();
    }

    /// <summary>
    /// Just enough of a Pusher server for room tests: presence membership and
    /// client-event relay with user_id. Signatures are not checked.
    /// </summary>
    sealed class FakeServer
    {
        public double Now;
        public readonly List<HibiraClient> Clients = new List<HibiraClient>();
        readonly Dictionary<string, List<(FakeTransport socket, string userId, object info)>> _channels =
            new Dictionary<string, List<(FakeTransport, string, object)>>();
        int _nextSocket;
        long _joinClock = 1_700_000_000_000;

        public HibiraClient NewClient(string userId, string name, Dictionary<string, object> extraInfo = null)
        {
            HibiraClient client = null;
            client = new HibiraClient(new HibiraOptions
            {
                AppKey = "key",
                ManualTick = true,
                TransportFactory = () => new FakeTransport { Server = this },
                Authorizer = (socketId, channel) =>
                {
                    var userInfo = new Dictionary<string, object> { ["name"] = name, ["joined_at"] = ++_joinClock };
                    if (extraInfo != null && channel.StartsWith("presence-queue-"))
                        foreach (var kv in extraInfo) userInfo[kv.Key] = kv.Value;
                    var channelData = Json.Serialize(new Dictionary<string, object> { ["user_id"] = userId, ["user_info"] = userInfo });
                    return Task.FromResult(new HibiraAuth("key:sig", channelData));
                },
            });
            client.Clock = () => Now;
            Clients.Add(client);
            return client;
        }

        /// <summary>Tick every client until nothing is left to deliver.</summary>
        public void Settle()
        {
            for (var i = 0; i < 20; i++)
                foreach (var c in Clients.ToArray()) c.Tick();
        }

        /// <summary>Move the clock and settle.</summary>
        public void Advance(double seconds)
        {
            Now += seconds;
            Settle();
        }

        public void Accept(FakeTransport socket)
        {
            socket.Incoming.Enqueue(TransportEvent.Opened());
            var data = Json.Serialize(new Dictionary<string, object> { ["socket_id"] = $"{++_nextSocket}.1", ["activity_timeout"] = 120 });
            socket.Push(new Dictionary<string, object> { ["event"] = "pusher:connection_established", ["data"] = data });
        }

        public void Receive(FakeTransport socket, string text)
        {
            var msg = (Dictionary<string, object>)Json.Parse(text);
            var ev = (string)msg["event"];
            var data = msg.TryGetValue("data", out var d) ? d as Dictionary<string, object> : null;
            if (ev == "pusher:subscribe")
            {
                var channel = (string)data["channel"];
                string userId = null;
                object info = null;
                if (data.TryGetValue("channel_data", out var cd))
                {
                    var parsed = (Dictionary<string, object>)Json.Parse((string)cd);
                    userId = (string)parsed["user_id"];
                    info = parsed["user_info"];
                }
                if (!_channels.TryGetValue(channel, out var members)) _channels[channel] = members = new List<(FakeTransport, string, object)>();
                var isNew = userId != null && members.All(m => m.userId != userId);
                members.Add((socket, userId, info));
                object payload = new Dictionary<string, object>();
                if (channel.StartsWith("presence-"))
                {
                    var hash = new Dictionary<string, object>();
                    foreach (var m in members) hash[m.userId] = m.info;
                    payload = new Dictionary<string, object>
                    {
                        ["presence"] = new Dictionary<string, object> { ["ids"] = hash.Keys.ToList(), ["hash"] = hash, ["count"] = hash.Count },
                    };
                }
                socket.Push(new Dictionary<string, object> { ["event"] = "pusher_internal:subscription_succeeded", ["channel"] = channel, ["data"] = Json.Serialize(payload) });
                if (isNew)
                {
                    var added = Json.Serialize(new Dictionary<string, object> { ["user_id"] = userId, ["user_info"] = info });
                    foreach (var m in members.Where(m => m.socket != socket))
                        m.socket.Push(new Dictionary<string, object> { ["event"] = "pusher_internal:member_added", ["channel"] = channel, ["data"] = added });
                }
            }
            else if (ev == "pusher:unsubscribe")
            {
                Leave(socket, (string)data["channel"]);
            }
            else if (ev.StartsWith("client-"))
            {
                var channel = (string)msg["channel"];
                if (!_channels.TryGetValue(channel, out var members)) return;
                var sender = members.FirstOrDefault(m => m.socket == socket);
                if (sender.socket == null) return;
                var relay = new Dictionary<string, object> { ["event"] = ev, ["channel"] = channel, ["data"] = msg["data"] };
                if (sender.userId != null) relay["user_id"] = sender.userId;
                foreach (var m in members.Where(m => m.socket != socket)) m.socket.Push(relay);
            }
        }

        public void Drop(FakeTransport socket)
        {
            foreach (var channel in _channels.Keys.ToList()) Leave(socket, channel);
        }

        void Leave(FakeTransport socket, string channel)
        {
            if (!_channels.TryGetValue(channel, out var members)) return;
            var gone = members.Where(m => m.socket == socket).ToList();
            members.RemoveAll(m => m.socket == socket);
            foreach (var g in gone)
            {
                if (g.userId == null || members.Any(m => m.userId == g.userId)) continue;
                var removed = Json.Serialize(new Dictionary<string, object> { ["user_id"] = g.userId });
                foreach (var m in members)
                    m.socket.Push(new Dictionary<string, object> { ["event"] = "pusher_internal:member_removed", ["channel"] = channel, ["data"] = removed });
            }
        }
    }
}
