using System;
using System.Collections.Generic;
using System.Linq;

namespace Hibira
{
    /// <summary>A match found by a <see cref="HibiraQueue"/>.</summary>
    public sealed class HibiraMatchFound
    {
        public string Id { get; }
        /// <summary>The match room's presence channel, e.g. "presence-match-&lt;id&gt;".</summary>
        public string Channel { get; }
        /// <summary>Matched players in queue order (Ready and Host are always false here).</summary>
        public IReadOnlyList<HibiraPlayer> Players { get; }
        /// <summary>The match room, already joining (null with AutoRoom off).</summary>
        public HibiraRoom Room { get; }

        public HibiraMatchFound(string id, string channel, IReadOnlyList<HibiraPlayer> players, HibiraRoom room)
        {
            Id = id;
            Channel = channel;
            Players = players;
            Room = room;
        }
    }

    public class HibiraQueueOptions : HibiraMatchRules
    {
        /// <summary>Seconds before giving up with <see cref="HibiraQueue.TimedOut"/>; infinity waits forever.</summary>
        public double Timeout = 60;
        /// <summary>Seconds between matching rounds on the queue host.</summary>
        public double Interval = 1;
        /// <summary>Match room channel prefix.</summary>
        public string RoomPrefix = "presence-match-";
        /// <summary>Join the match room as a <see cref="HibiraRoom"/>.</summary>
        public bool AutoRoom = true;
        /// <summary>Options for the match room; Capacity is set to the match size.</summary>
        public HibiraRoomOptions RoomOptions;
        public int MaxEventBytes = 3000;
        public int MaxEventsPerTick = 3;
    }

    /// <summary>
    /// Matchmaking queue on a presence channel, compatible with Queue in
    /// @hibira-io/lobby 0.2, so Unity and browser players can wait in the same
    /// queue. Your auth endpoint signs joined_at, rating and criteria into
    /// user_info. The player who has waited longest is the queue host: it runs
    /// <see cref="HibiraMatch.FindMatches"/> and announces matches with
    /// client-queue-match. Matched players leave the queue and join the room
    /// presence-match-&lt;id&gt;.
    /// </summary>
    public sealed class HibiraQueue
    {
        public const string EventMatch = "client-queue-match";

        /// <summary>Subscribed. Arguments: your position, players waiting.</summary>
        public event Action<int, int> Joined;
        /// <summary>The queue changed. Arguments: your position, players waiting.</summary>
        public event Action<int, int> Updated;
        public event Action<HibiraMatchFound> Matched;
        /// <summary>No match within the timeout. Argument: seconds waited.</summary>
        public event Action<double> TimedOut;
        public event Action<int, string> JoinFailed;

        public HibiraChannel Channel { get; }
        public string Name => Channel.Name;
        public HibiraQueueOptions Options { get; }
        /// <summary>"waiting", then "matched", "timeout" or "left".</summary>
        public string Status { get; private set; } = "waiting";
        public bool IsJoined { get; private set; }
        public HibiraMatchFound Match { get; private set; }

        /// <summary>Wall clock in ms since epoch, compared with the signed joined_at.</summary>
        internal Func<double> EpochMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        readonly WeakReference<HibiraClient> _client;
        readonly HashSet<string> _claimed = new HashSet<string>();
        readonly Action<object> _onSubscribed;
        readonly Action<int, string> _onSubscriptionError;
        readonly Action<string, object> _onMemberAdded;
        readonly Action<string, object> _onMemberRemoved;
        readonly Action<HibiraEvent> _onEvent;
        string _host;
        int _tickTimer;
        int _timeoutTimer;
        bool _ownsSubscription;
        bool _left;

        /// <summary>Subscribe to <paramref name="channelName"/> (a presence- channel) and wait for a match.</summary>
        public static HibiraQueue Join(HibiraClient client, string channelName, HibiraQueueOptions options = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (channelName == null || !channelName.StartsWith("presence-", StringComparison.Ordinal) || channelName.StartsWith("presence-large-", StringComparison.Ordinal))
                throw new ArgumentException($"HibiraQueue: {channelName} is not a presence channel (presence-large- channels do not allow client events)", nameof(channelName));
            var queue = new HibiraQueue(client.Subscribe(channelName), options);
            queue._ownsSubscription = true;
            return queue;
        }

        /// <summary>
        /// Track a channel you subscribed to yourself. Every client should use
        /// the same matching options; the host's are the ones that count.
        /// </summary>
        public HibiraQueue(HibiraChannel channel, HibiraQueueOptions options = null)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            var client = channel.Client ?? throw new InvalidOperationException("HibiraQueue: the channel's client is gone");
            _client = new WeakReference<HibiraClient>(client);
            Options = options ?? new HibiraQueueOptions();
            _onSubscribed = _ => OnSubscribed();
            _onSubscriptionError = (status, message) => JoinFailed?.Invoke(status, message);
            _onMemberAdded = (id, _) => OnMemberAdded(id);
            _onMemberRemoved = (id, _) => OnMemberRemoved(id);
            _onEvent = OnEvent;
            channel.Subscribed += _onSubscribed;
            channel.SubscriptionError += _onSubscriptionError;
            channel.MemberAdded += _onMemberAdded;
            channel.MemberRemoved += _onMemberRemoved;
            channel.EventReceived += _onEvent;
            if (channel.IsSubscribed && channel.Members.Count > 0) OnSubscribed();
        }

        // ---- reading the queue -----------------------------------------------

        public string MyId => Channel.MyId;

        /// <summary>Players still waiting (matched players on their way out excluded), longest wait first.</summary>
        public IReadOnlyList<HibiraTicket> Tickets
        {
            get
            {
                var list = Channel.Members
                    .Where(kv => !_claimed.Contains(kv.Key))
                    .Select(kv => HibiraTicket.FromMember(kv.Key, kv.Value))
                    .ToList();
                list.Sort((a, b) => a.JoinedAt != b.JoinedAt ? a.JoinedAt.CompareTo(b.JoinedAt) : string.CompareOrdinal(a.Id, b.Id));
                return list;
            }
        }

        public int Count => Tickets.Count;

        /// <summary>Your place in the queue, 1 for the longest wait; 0 when not waiting.</summary>
        public int Position
        {
            get
            {
                var tickets = Tickets;
                for (var i = 0; i < tickets.Count; i++)
                    if (tickets[i].Id == MyId) return i + 1;
                return 0;
            }
        }

        /// <summary>Seconds you have waited, by your signed joined_at.</summary>
        public double Waited
        {
            get
            {
                if (MyId == null || !Channel.Members.TryGetValue(MyId, out var info)) return 0;
                var t = HibiraTicket.FromMember(MyId, info).JoinedAt;
                return t == double.MaxValue ? 0 : Math.Max(0, (EpochMs() - t) / 1000);
            }
        }

        /// <summary>The rating range you currently accept.</summary>
        public double Range => HibiraMatch.AllowedRange(Waited, Options);

        public bool IsHost => MyId != null && _host == MyId;

        // ---- actions ---------------------------------------------------------

        /// <summary>Leave the queue without a match.</summary>
        public void Leave() => Close("left");

        HibiraClient Client => _client.TryGetTarget(out var c) ? c : null;

        void Close(string status)
        {
            if (_left) return;
            _left = true;
            if (Status == "waiting") Status = status;
            Channel.Subscribed -= _onSubscribed;
            Channel.SubscriptionError -= _onSubscriptionError;
            Channel.MemberAdded -= _onMemberAdded;
            Channel.MemberRemoved -= _onMemberRemoved;
            Channel.EventReceived -= _onEvent;
            var client = Client;
            client?.Cancel(_tickTimer);
            client?.Cancel(_timeoutTimer);
            _tickTimer = _timeoutTimer = 0;
            if (_ownsSubscription) Channel.Unsubscribe();
        }

        // ---- presence --------------------------------------------------------

        void OnSubscribed()
        {
            IsJoined = true;
            Recompute();
            var client = Client;
            if (client != null && !double.IsInfinity(Options.Timeout) && Options.Timeout >= 0)
                _timeoutTimer = client.After(Options.Timeout, OnTimeout);
            Joined?.Invoke(Position, Count);
            Tick();
        }

        void OnMemberAdded(string id)
        {
            _claimed.Remove(id);
            Recompute();
            Updated?.Invoke(Position, Count);
            Tick();
        }

        void OnMemberRemoved(string id)
        {
            _claimed.Remove(id);
            Recompute();
            Updated?.Invoke(Position, Count);
        }

        void Recompute()
        {
            var wasHost = IsHost;
            var ids = Channel.Members.Select(kv => HibiraTicket.FromMember(kv.Key, kv.Value)).ToList();
            ids.Sort((a, b) => a.JoinedAt != b.JoinedAt ? a.JoinedAt.CompareTo(b.JoinedAt) : string.CompareOrdinal(a.Id, b.Id));
            _host = ids.Count > 0 ? ids[0].Id : null;
            if (IsHost && !wasHost) ScheduleTick();
            else if (!IsHost && wasHost)
            {
                Client?.Cancel(_tickTimer);
                _tickTimer = 0;
            }
        }

        void ScheduleTick()
        {
            var client = Client;
            if (client == null || _left) return;
            client.Cancel(_tickTimer);
            _tickTimer = client.After(Options.Interval, () =>
            {
                _tickTimer = 0;
                Tick();
                if (IsHost && !_left) ScheduleTick();
            });
        }

        // ---- matching (host only) --------------------------------------------

        internal void Tick()
        {
            if (_left || !IsJoined || !IsHost) return;
            var found = HibiraMatch.FindMatches(Tickets, Options, EpochMs())
                .Select(g => new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["players"] = g.Select(t => (object)t.Id).ToList(),
                })
                .ToList();
            if (found.Count == 0) return;

            // Batches keep each event under the 4 KB Free plan limit, and a few
            // per round stay under the client event rate limit.
            var batches = new List<List<Dictionary<string, object>>>();
            var batch = new List<Dictionary<string, object>>();
            foreach (var m in found)
            {
                if (batch.Count > 0 && Json.Serialize(Wrap(batch.Concat(new[] { m }).ToList())).Length > Options.MaxEventBytes)
                {
                    batches.Add(batch);
                    batch = new List<Dictionary<string, object>>();
                }
                batch.Add(m);
            }
            if (batch.Count > 0) batches.Add(batch);

            foreach (var b in batches.Take(Math.Max(1, Options.MaxEventsPerTick)))
            {
                if (!Channel.Trigger(EventMatch, Wrap(b)))
                {
#if UNITY_5_3_OR_NEWER
                    UnityEngine.Debug.LogWarning($"HibiraQueue: could not send {EventMatch} (not connected?)");
#endif
                    return;
                }
                // Our own event is not echoed back, so apply it here too.
                ApplyMatches(b.Cast<object>().ToList());
                if (_left) return;
            }
        }

        static Dictionary<string, object> Wrap(List<Dictionary<string, object>> matches) =>
            new Dictionary<string, object> { ["matches"] = matches };

        // ---- client events ---------------------------------------------------

        void OnEvent(HibiraEvent ev)
        {
            if (_left || ev.Name != EventMatch) return;
            if (ev.UserId == null || ev.UserId != _host) return;
            var d = ev.Data as Dictionary<string, object>;
            if (Json.Get(d, "matches") is List<object> matches) ApplyMatches(matches);
        }

        void ApplyMatches(List<object> matches)
        {
            Dictionary<string, object> mine = null;
            List<string> minePlayers = null;
            foreach (var item in matches)
            {
                if (!(item is Dictionary<string, object> m) || !(Json.Get(m, "id") is string) || !(Json.Get(m, "players") is IEnumerable<object> raw)) continue;
                var players = raw.Select(Json.ToText).ToList();
                // A player already claimed by an earlier match cannot be in a second one.
                if (players.Any(_claimed.Contains)) continue;
                foreach (var id in players) _claimed.Add(id);
                if (mine == null && players.Contains(MyId))
                {
                    mine = m;
                    minePlayers = players;
                }
            }
            if (mine != null) OnMatched((string)mine["id"], minePlayers);
            else Updated?.Invoke(Position, Count);
        }

        void OnMatched(string id, List<string> playerIds)
        {
            if (Status != "waiting" || _left) return;
            Status = "matched";
            var channel = Options.RoomPrefix + id;
            var players = playerIds
                .Select(p => new HibiraPlayer(p, Channel.Members.TryGetValue(p, out var info) ? info : null, false, false, p == MyId))
                .ToList();
            HibiraRoom room = null;
            var client = Client;
            if (Options.AutoRoom && client != null)
            {
                var o = Options.RoomOptions ?? new HibiraRoomOptions();
                room = HibiraRoom.Join(client, channel, new HibiraRoomOptions
                {
                    Capacity = players.Count,
                    LeaveWhenFull = o.LeaveWhenFull,
                    MaxMessages = o.MaxMessages,
                    TypingTimeout = o.TypingTimeout,
                    StateDelay = o.StateDelay,
                });
            }
            Match = new HibiraMatchFound(id, channel, players, room);
            Matched?.Invoke(Match);
            Close("matched");
        }

        void OnTimeout()
        {
            _timeoutTimer = 0;
            if (Status != "waiting" || _left) return;
            Status = "timeout";
            var waited = Waited;
            Close("timeout");
            TimedOut?.Invoke(waited);
        }
    }
}
