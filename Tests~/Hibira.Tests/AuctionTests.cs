using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    static class AuctionTests
    {
        static Dictionary<string, object> L(string id, long version, string status = "open", double endsIn = 60000, long? price = null, string type = "sword", double closedAt = 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var d = new Dictionary<string, object>
            {
                ["id"] = id,
                ["version"] = version,
                ["status"] = status,
                ["endsAt"] = now + endsIn,
                ["startPrice"] = 10L,
                ["minBid"] = (price ?? 10) + 1,
                ["bids"] = price == null ? 0L : 1L,
                ["sellerId"] = "s",
                ["item"] = new Dictionary<string, object> { ["type"] = type },
            };
            if (price != null) d["price"] = price.Value;
            if (status != "open") d["closedAt"] = closedAt == 0 ? (double)now : closedAt;
            return d;
        }

        static Dictionary<string, object> Update(string kind, Dictionary<string, object> listing) =>
            new Dictionary<string, object> { ["kind"] = kind, ["listing"] = listing };

        static (HibiraAuctionHouse house, Func<string> setSnap) Setup(string snapshot)
        {
            var snap = snapshot;
            var channel = new HibiraChannel(new HibiraClient("key"), "auction-main");
            var house = new HibiraAuctionHouse(channel, "main", () => Task.FromResult(snap));
            return (house, () => snap);
        }

        [Test]
        static void LoadsTheSnapshotAndSortsByEnd()
        {
            var snap = Json.Serialize(new Dictionary<string, object>
            {
                ["serverNow"] = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["listings"] = new List<object> { L("a", 1, endsIn: 60000), L("b", 1, endsIn: 1000) },
                ["closed"] = new List<object> { L("c", 3, "sold", price: 40) },
                ["prices"] = new Dictionary<string, object> { ["sword"] = new List<object> { new Dictionary<string, object> { ["price"] = 30L, ["at"] = 1.0, ["listingId"] = "old" } } },
            });
            var (house, _) = Setup(snap);
            var ready = 0;
            house.Ready += () => ready++;
            house.Reload().Wait();
            Assert.Equal(1, ready);
            Assert.Equal("b,a", string.Join(",", house.Listings.Select(l => l.Id)));
            Assert.Equal("c", house.Closed[0].Id);
            Assert.Equal("30,40", string.Join(",", house.Prices("sword").Select(p => p.Price)));
            Assert.Equal(11L, house.Get("a").MinBid);
            Assert.Equal("sword", house.Get("a").ItemType);
        }

        [Test]
        static void KeepsTheNewestVersion()
        {
            var (house, _) = Setup("{\"listings\":[]}");
            house.Reload().Wait();
            var kinds = new List<string>();
            house.Updated += (k, l) => kinds.Add(k);
            house.Apply(Update("bid", L("a", 3, price: 50)));
            house.Apply(Update("bid", L("a", 2, price: 40)));
            Assert.Equal(50L, house.Get("a").Price);
            Assert.Equal("bid", string.Join(",", kinds));
            house.Apply(Update("sold", L("a", 4, "sold", price: 60)));
            Assert.Equal(0, house.Listings.Count);
            Assert.Equal("sold", house.Closed[0].Status);
            Assert.Equal("60", string.Join(",", house.Prices("sword").Select(p => p.Price)));
        }

        [Test]
        static void EventsDuringLoadingWinWhenNewer()
        {
            var tcs = new TaskCompletionSource<string>();
            var channel = new HibiraChannel(new HibiraClient("key"), "auction-main");
            var house = new HibiraAuctionHouse(channel, "main", () => tcs.Task);
            var loading = house.Reload();
            house.Apply(Update("bid", L("a", 5, price: 90)));
            house.Apply(Update("listed", L("new", 1)));
            tcs.SetResult(Json.Serialize(new Dictionary<string, object>
            {
                ["listings"] = new List<object> { L("a", 4, price: 80), L("b", 1) },
            }));
            loading.Wait();
            Assert.Equal(90L, house.Get("a").Price);
            Assert.Equal("a,b,new", string.Join(",", house.Listings.Select(l => l.Id).OrderBy(x => x)));
        }

        [Test]
        static void DropsEndedListingsTheSnapshotNoLongerHas()
        {
            var (house, _) = Setup("{\"listings\":[]}");
            house.Apply(Update("listed", L("gone", 1, endsIn: -1000)));
            house.Reload().Wait();
            Assert.Equal(0, house.Listings.Count);
        }

        [Test]
        static void TimeLeftFollowsTheServerClock()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var l = L("a", 1);
            l["endsAt"] = (double)(now + 15000);
            var (house, _) = Setup(Json.Serialize(new Dictionary<string, object> { ["serverNow"] = (double)(now + 10000), ["listings"] = new List<object> { l } }));
            house.Reload().Wait();
            var left = house.TimeLeft(house.Get("a"));
            Assert.True(left > 4 && left <= 5, $"left {left}");
        }

        [Test]
        static void ReportsLoadFailuresAndIgnoresMalformedEvents()
        {
            var channel = new HibiraChannel(new HibiraClient("key"), "auction-main");
            var house = new HibiraAuctionHouse(channel, "main", () => Task.FromException<string>(new Exception("500")));
            var errors = new List<string>();
            house.Failed += errors.Add;
            house.Reload().Wait();
            Assert.Equal("500", string.Join(",", errors));
            house.Apply(null);
            house.Apply(new Dictionary<string, object> { ["listing"] = new Dictionary<string, object> { ["id"] = 1L } });
            Assert.Equal(0, house.Listings.Count);
        }

        [Test]
        static void RefusesPresenceChannels()
        {
            Assert.Throws<ArgumentException>(() => HibiraAuctionHouse.Open(new HibiraClient("key"), "x", () => Task.FromResult("{}"), "presence-auction"));
        }
    }
}
