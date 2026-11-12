using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hibira
{
    /// <summary>One listing of an auction house, as the server published it.</summary>
    public sealed class HibiraListing
    {
        public string Id { get; }
        public string House { get; }
        public string SellerId { get; }
        /// <summary>Public info about the seller (name...), as your server set it.</summary>
        public Dictionary<string, object> Seller { get; }
        /// <summary>What is sold, as your server set it; Item["type"] groups sale prices.</summary>
        public Dictionary<string, object> Item { get; }
        public long StartPrice { get; }
        public long? Buyout { get; }
        /// <summary>The highest bid, or the sale price once sold; null before the first bid.</summary>
        public long? Price { get; }
        public string BidderId { get; }
        public Dictionary<string, object> Bidder { get; }
        public int Bids { get; }
        /// <summary>The lowest bid the server accepts next.</summary>
        public long MinBid { get; }
        /// <summary>Server time, ms since epoch.</summary>
        public double EndsAt { get; }
        /// <summary>"open", "sold", "unsold" or "cancelled".</summary>
        public string Status { get; }
        public double? ClosedAt { get; }
        public long Version { get; }
        /// <summary>The listing as JSON values, for fields your server added.</summary>
        public Dictionary<string, object> Raw { get; }

        public bool IsOpen => Status == "open";
        /// <summary>Item["type"] as text, or null.</summary>
        public string ItemType => Json.Get(Item, "type") is object t ? Json.ToText(t) : null;

        HibiraListing(Dictionary<string, object> d)
        {
            Raw = d;
            Id = Json.ToText(Json.Get(d, "id"));
            House = Json.Get(d, "house") as string;
            SellerId = Json.Get(d, "sellerId") is object s ? Json.ToText(s) : null;
            Seller = Json.Get(d, "seller") as Dictionary<string, object> ?? new Dictionary<string, object>();
            Item = Json.Get(d, "item") as Dictionary<string, object> ?? new Dictionary<string, object>();
            StartPrice = (long)Json.ToDouble(Json.Get(d, "startPrice"));
            Buyout = Long(Json.Get(d, "buyout"));
            Price = Long(Json.Get(d, "price"));
            BidderId = Json.Get(d, "bidderId") is object b ? Json.ToText(b) : null;
            Bidder = Json.Get(d, "bidder") as Dictionary<string, object>;
            Bids = (int)Json.ToDouble(Json.Get(d, "bids"));
            MinBid = (long)Json.ToDouble(Json.Get(d, "minBid"));
            EndsAt = Json.ToDouble(Json.Get(d, "endsAt"));
            Status = Json.Get(d, "status") as string ?? "open";
            ClosedAt = Json.IsNumber(Json.Get(d, "closedAt")) ? Json.ToDouble(Json.Get(d, "closedAt")) : (double?)null;
            Version = (long)Json.ToDouble(Json.Get(d, "version"));
        }

        /// <summary>Read a listing from parsed JSON; null when it has no id or version.</summary>
        public static HibiraListing From(object value)
        {
            if (!(value is Dictionary<string, object> d)) return null;
            if (!(Json.Get(d, "id") is string id) || id.Length == 0 || !Json.IsNumber(Json.Get(d, "version"))) return null;
            return new HibiraListing(d);
        }

        static long? Long(object v) => Json.IsNumber(v) ? (long)Json.ToDouble(v) : (long?)null;
    }

    /// <summary>A recent sale price of one item type.</summary>
    public readonly struct HibiraPricePoint
    {
        public readonly long Price;
        public readonly double At;
        public readonly string ListingId;

        public HibiraPricePoint(long price, double at, string listingId)
        {
            Price = price;
            At = at;
            ListingId = listingId;
        }
    }

    /// <summary>
    /// An auction house that follows a channel published by @hibira-io/auction's
    /// server (createAuctionHouse in Node), so Unity and browser players share
    /// the same house. The channel carries one event, auction-update, with the
    /// whole listing and a version; the newest version of each listing wins.
    /// On every subscription, including after a reconnect, it loads the
    /// server's snapshot (house.snapshot()) so nothing is missed.
    ///
    /// Players never bid over the socket: send bids to your own server, which
    /// checks them with house.bid().
    /// </summary>
    public sealed class HibiraAuctionHouse
    {
        public const string EventUpdate = "auction-update";

        /// <summary>The first snapshot has loaded.</summary>
        public event Action Ready;
        /// <summary>Something changed: a server event or a (re)load.</summary>
        public event Action Changed;
        /// <summary>One server event. Arguments: kind (listed, bid, extended, sold, unsold, cancelled) and the listing.</summary>
        public event Action<string, HibiraListing> Updated;
        /// <summary>Loading the snapshot or subscribing failed. Argument: a message.</summary>
        public event Action<string> Failed;

        public HibiraChannel Channel { get; }
        public string Name { get; }
        public bool IsReady { get; private set; }
        /// <summary>Server clock minus this device's clock, in ms.</summary>
        public double OffsetMs { get; private set; }
        public int KeepClosed { get; set; } = 50;
        public int HistoryPerType { get; set; } = 20;

        /// <summary>Wall clock in ms since epoch.</summary>
        internal Func<double> EpochMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        readonly Func<Task<string>> _load;
        readonly Action<HibiraEvent> _onUpdate;
        readonly Action<object> _onSubscribed;
        readonly Action<int, string> _onError;
        Dictionary<string, HibiraListing> _open = new Dictionary<string, HibiraListing>();
        List<HibiraListing> _closed = new List<HibiraListing>();
        readonly Dictionary<string, List<HibiraPricePoint>> _prices = new Dictionary<string, List<HibiraPricePoint>>();
        int _loadSeq;

        /// <summary>
        /// Subscribe to auction-&lt;house&gt; and load the snapshot from
        /// <paramref name="snapshotUrl"/> (your server's GET returning house.snapshot()).
        /// </summary>
        public static HibiraAuctionHouse Open(HibiraClient client, string house, string snapshotUrl, string channel = null) =>
            Open(client, house, async () =>
            {
                var res = await HibiraHttp.Get(snapshotUrl);
                if (res.Status < 200 || res.Status >= 300) throw new Exception(res.Error ?? $"snapshot: HTTP {res.Status}");
                return res.Body;
            }, channel);

        /// <summary>Subscribe to auction-&lt;house&gt;; <paramref name="load"/> returns the snapshot as JSON text.</summary>
        public static HibiraAuctionHouse Open(HibiraClient client, string house, Func<Task<string>> load, string channel = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var name = channel ?? "auction-" + house;
            if (name.StartsWith("presence-", StringComparison.Ordinal))
                throw new ArgumentException($"HibiraAuctionHouse: use a public or private- channel, not \"{name}\"");
            return new HibiraAuctionHouse(client.Subscribe(name), house, load);
        }

        /// <summary>Track a channel you subscribed to yourself. Create it right after subscribing.</summary>
        public HibiraAuctionHouse(HibiraChannel channel, string house, Func<Task<string>> load)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            Name = house;
            _onUpdate = ev => Apply(ev.Data);
            _onSubscribed = _ => _ = Reload();
            _onError = (status, message) => Failed?.Invoke($"subscription failed ({status}): {message}");
            channel.Bind(EventUpdate, _onUpdate);
            channel.Subscribed += _onSubscribed;
            channel.SubscriptionError += _onError;
            if (channel.IsSubscribed) _ = Reload();
        }

        /// <summary>Open listings, ending soonest first.</summary>
        public IReadOnlyList<HibiraListing> Listings =>
            _open.Values.OrderBy(l => l.EndsAt).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();

        /// <summary>Recently closed listings, newest first.</summary>
        public IReadOnlyList<HibiraListing> Closed => _closed.ToList();

        public HibiraListing Get(string id) =>
            id != null && _open.TryGetValue(id, out var l) ? l : _closed.FirstOrDefault(c => c.Id == id);

        /// <summary>The server's clock in ms since epoch.</summary>
        public double NowMs() => EpochMs() + OffsetMs;

        /// <summary>Seconds left on a listing (0 once ended), on the server's clock.</summary>
        public double TimeLeft(HibiraListing listing) =>
            listing == null || !listing.IsOpen ? 0 : Math.Max(0, (listing.EndsAt - NowMs()) / 1000.0);

        /// <summary>Recent sale prices of an item type, oldest first.</summary>
        public IReadOnlyList<HibiraPricePoint> Prices(string type) =>
            type != null && _prices.TryGetValue(type, out var list) ? list.ToList() : new List<HibiraPricePoint>();

        /// <summary>Load the snapshot again (done for you on every subscription).</summary>
        public async Task Reload()
        {
            var seq = ++_loadSeq;
            string text;
            try
            {
                text = await _load();
            }
            catch (Exception e)
            {
                if (seq == _loadSeq) Failed?.Invoke(e.Message);
                return;
            }
            if (seq != _loadSeq) return;
            if (!Json.TryParse(text, out var parsed) || !(parsed is Dictionary<string, object> snap))
            {
                Failed?.Invoke("snapshot: not a JSON object");
                return;
            }
            Merge(snap);
            var first = !IsReady;
            IsReady = true;
            if (first) Ready?.Invoke();
            Changed?.Invoke();
        }

        /// <summary>Stop listening and unsubscribe.</summary>
        public void Close()
        {
            _loadSeq++;
            Channel.Unbind(EventUpdate, _onUpdate);
            Channel.Subscribed -= _onSubscribed;
            Channel.SubscriptionError -= _onError;
            Channel.Unsubscribe();
        }

        // ---- internals -------------------------------------------------------

        void Merge(Dictionary<string, object> snap)
        {
            if (Json.IsNumber(Json.Get(snap, "serverNow"))) OffsetMs = Json.ToDouble(Json.Get(snap, "serverNow")) - EpochMs();
            var all = new Dictionary<string, HibiraListing>();
            foreach (var l in _closed) all[l.Id] = l;
            foreach (var l in _open.Values) all[l.Id] = l;
            var listed = new HashSet<string>();
            foreach (var key in new[] { "listings", "closed" })
            {
                if (!(Json.Get(snap, key) is List<object> list)) continue;
                foreach (var item in list)
                {
                    var l = HibiraListing.From(item);
                    if (l == null) continue;
                    listed.Add(l.Id);
                    if (!all.TryGetValue(l.Id, out var mine) || mine.Version < l.Version) all[l.Id] = l;
                }
            }
            // An open listing the snapshot does not mention arrived after it,
            // unless its time is up (closed and pruned on the server).
            var now = NowMs();
            foreach (var id in all.Keys.ToList())
            {
                var l = all[id];
                if (!listed.Contains(id) && l.IsOpen && l.EndsAt <= now) all.Remove(id);
            }
            _open = all.Values.Where(l => l.IsOpen).ToDictionary(l => l.Id);
            _closed = all.Values.Where(l => !l.IsOpen).OrderByDescending(l => l.ClosedAt ?? 0).Take(KeepClosed).ToList();
            if (Json.Get(snap, "prices") is Dictionary<string, object> prices)
            {
                foreach (var kv in prices)
                {
                    if (!(kv.Value is List<object> points)) continue;
                    foreach (var p in points.OfType<Dictionary<string, object>>())
                        AddPrice(kv.Key, new HibiraPricePoint((long)Json.ToDouble(Json.Get(p, "price")), Json.ToDouble(Json.Get(p, "at")), Json.ToText(Json.Get(p, "listingId"))));
                }
            }
            foreach (var l in _closed) NotePrice(l);
        }

        internal void Apply(object data)
        {
            var d = data as Dictionary<string, object>;
            var l = HibiraListing.From(Json.Get(d, "listing"));
            if (l == null) return;
            if (Json.IsNumber(Json.Get(d, "serverNow"))) OffsetMs = Json.ToDouble(Json.Get(d, "serverNow")) - EpochMs();
            var known = Get(l.Id);
            if (known != null && known.Version >= l.Version) return;
            if (l.IsOpen)
            {
                _open[l.Id] = l;
            }
            else
            {
                _open.Remove(l.Id);
                _closed.RemoveAll(c => c.Id == l.Id);
                _closed.Insert(0, l);
                if (_closed.Count > KeepClosed) _closed.RemoveRange(KeepClosed, _closed.Count - KeepClosed);
                NotePrice(l);
            }
            Updated?.Invoke(Json.Get(d, "kind") as string, l);
            Changed?.Invoke();
        }

        void NotePrice(HibiraListing l)
        {
            if (l.Status != "sold" || l.ItemType == null || l.Price == null) return;
            AddPrice(l.ItemType, new HibiraPricePoint(l.Price.Value, l.ClosedAt ?? 0, l.Id));
        }

        void AddPrice(string type, HibiraPricePoint point)
        {
            if (!_prices.TryGetValue(type, out var list)) _prices[type] = list = new List<HibiraPricePoint>();
            if (list.Any(p => p.ListingId == point.ListingId)) return;
            list.Add(point);
            list.Sort((a, b) => a.At.CompareTo(b.At));
            if (list.Count > HistoryPerType) list.RemoveRange(0, list.Count - HistoryPerType);
        }
    }
}
