using System;
using System.Collections.Generic;
using System.Linq;

namespace Hibira.Tests
{
    static class QueueTests
    {
        const string Channel = "presence-queue-duo";
        const double Epoch = 1_700_000_000_000;

        static HibiraTicket T(string id, double joinedAt, double rating = 0, Dictionary<string, object> criteria = null) =>
            new HibiraTicket(id, joinedAt, rating, criteria);

        static string Groups(IEnumerable<List<HibiraTicket>> matches) =>
            string.Join("|", matches.Select(g => string.Join(",", g.Select(t => t.Id))));

        static (FakeServer server, HibiraQueue[] queues) Setup(double[] ratings, HibiraQueueOptions options)
        {
            var server = new FakeServer();
            var queues = new List<HibiraQueue>();
            for (var i = 0; i < ratings.Length; i++)
            {
                var client = server.NewClient($"u{i + 1}", $"P{i + 1}", new Dictionary<string, object> { ["rating"] = ratings[i] });
                client.Connect();
                var q = HibiraQueue.Join(client, Channel, options);
                q.EpochMs = () => Epoch + 1000 + server.Now * 1000;
                queues.Add(q);
                server.Settle();
            }
            return (server, queues.ToArray());
        }

        // ---- rules (same cases as match tests in @hibira-io/lobby) -----------

        [Test]
        static void FindMatchesTakesOldestFirstAndClosestRating()
        {
            var m = HibiraMatch.FindMatches(new[] { T("a", 1, 1000), T("b", 2, 1500), T("c", 3, 1010), T("d", 4, 1490), T("e", 5, 3000) }, new HibiraMatchRules { Size = 2 }, 10_000);
            Assert.Equal("a,c|b,d", Groups(m));
        }

        [Test]
        static void RangeWidensWithWaitingUpToMaxRange()
        {
            var rules = new HibiraMatchRules { Size = 2, RatingRange = 100, WidenBy = 100, WidenEvery = 1, MaxRange = 250 };
            Assert.Equal(200.0, HibiraMatch.AllowedRange(1.5, rules));
            Assert.Equal(250.0, HibiraMatch.AllowedRange(60, rules));
            Assert.Equal(0, HibiraMatch.FindMatches(new[] { T("a", 0, 1000), T("b", 0, 1300) }, rules, 60_000).Count);
            Assert.Equal(1, HibiraMatch.FindMatches(new[] { T("a", 0, 1000), T("b", 0, 1240) }, rules, 60_000).Count);
        }

        [Test]
        static void SmallerMatchAfterFillAfter()
        {
            var rules = new HibiraMatchRules { Size = 4, MinSize = 2, FillAfter = 5 };
            var tickets = new[] { T("a", 0), T("b", 100), T("c", 200) };
            Assert.Equal(0, HibiraMatch.FindMatches(tickets, rules, 4000).Count);
            Assert.Equal("a,b,c", Groups(HibiraMatch.FindMatches(tickets, rules, 5000)));
        }

        [Test]
        static void CriteriaFitMatchesValuesAndArrays()
        {
            Dictionary<string, object> C(string k, object v) => new Dictionary<string, object> { [k] = v };
            Assert.True(HibiraMatch.CriteriaFit(C("mode", "duo"), C("mode", "duo")), "equal");
            Assert.True(!HibiraMatch.CriteriaFit(C("mode", "duo"), C("mode", "solo")), "different");
            Assert.True(HibiraMatch.CriteriaFit(C("region", new List<object> { "hkg", "tyo" }), C("region", "tyo")), "array overlap");
            Assert.True(!HibiraMatch.CriteriaFit(C("region", new List<object> { "hkg" }), C("region", new List<object> { "sin" })), "no overlap");
            Assert.True(HibiraMatch.CriteriaFit(C("mode", "duo"), new Dictionary<string, object>()), "wildcard");
            Assert.True(HibiraMatch.CriteriaFit(C("level", 3.0), C("level", 3L)), "numbers compare by value");
        }

        [Test]
        static void EveryPairInAGroupMustFit()
        {
            Dictionary<string, object> R(string r) => new Dictionary<string, object> { ["region"] = r };
            var tickets = new[] { T("a", 0), T("b", 1, 0, R("hkg")), T("c", 2, 0, R("tyo")) };
            Assert.Equal(0, HibiraMatch.FindMatches(tickets, new HibiraMatchRules { Size = 3 }, 10).Count);
            Assert.Equal("a,b", Groups(HibiraMatch.FindMatches(tickets, new HibiraMatchRules { Size = 2 }, 10)));
        }

        // ---- the queue -------------------------------------------------------

        [Test]
        static void RejectsNonPresenceChannels()
        {
            var client = new FakeServer().NewClient("u1", "Ann");
            Assert.Throws<ArgumentException>(() => HibiraQueue.Join(client, "private-queue"));
            Assert.Throws<ArgumentException>(() => HibiraQueue.Join(client, "presence-large-queue"));
        }

        [Test]
        static void MatchesClosestRatingsAndMovesEveryoneIntoTheirRoom()
        {
            var (server, q) = Setup(new double[] { 1000, 2000, 1050, 2040 }, new HibiraQueueOptions { Size = 2, RatingRange = 100, Timeout = double.PositiveInfinity });
            server.Settle();
            Assert.True(q.All(x => x.Status == "matched"), "all matched: " + string.Join(",", q.Select(x => x.Status)));
            Assert.Equal("u1,u3", string.Join(",", q[0].Match.Players.Select(p => p.Id)));
            Assert.Equal("u2,u4", string.Join(",", q[1].Match.Players.Select(p => p.Id)));
            Assert.Equal(q[0].Match.Channel, q[2].Match.Channel, "pair 1 room");
            Assert.Equal(q[1].Match.Channel, q[3].Match.Channel, "pair 2 room");
            Assert.True(q[0].Match.Channel != q[1].Match.Channel, "two rooms");
            Assert.True(q[0].Match.Channel.StartsWith("presence-match-"), "room prefix");
            var room = q[2].Match.Room;
            Assert.Equal(2, room.Capacity);
            Assert.Equal("u1,u3", string.Join(",", room.Players.Select(p => p.Id).OrderBy(x => x)));
        }

        [Test]
        static void HostWaitsThenMatchesOnTheNextJoin()
        {
            var (server, q) = Setup(new double[] { 1000 }, new HibiraQueueOptions { Size = 2, Timeout = double.PositiveInfinity });
            Assert.True(q[0].IsHost && q[0].Status == "waiting", "alone and waiting");
            Assert.Equal(1, q[0].Position);
            var c = server.NewClient("u2", "P2", new Dictionary<string, object> { ["rating"] = 1000.0 });
            c.Connect();
            var q2 = HibiraQueue.Join(c, Channel, new HibiraQueueOptions { Size = 2, Timeout = double.PositiveInfinity });
            server.Settle();
            Assert.Equal("matched", q[0].Status);
            Assert.Equal("matched", q2.Status);
            Assert.Equal(q[0].Match.Id, q2.Match.Id);
        }

        [Test]
        static void IgnoresMatchesFromAnyoneButTheHost()
        {
            var (server, q) = Setup(new double[] { 0, 5000, 9000 }, new HibiraQueueOptions { Size = 2, RatingRange = 10, Timeout = double.PositiveInfinity });
            var fake = new Dictionary<string, object>
            {
                ["matches"] = new List<object> { new Dictionary<string, object> { ["id"] = "x", ["players"] = new List<object> { "u2", "u3" } } },
            };
            q[1].Channel.Trigger(HibiraQueue.EventMatch, fake);
            server.Settle();
            Assert.Equal("waiting", q[2].Status);
            Assert.Equal(3, q[2].Count);
        }

        [Test]
        static void TimesOutAndLeaves()
        {
            var (server, q) = Setup(new double[] { 0 }, new HibiraQueueOptions { Size = 2, Timeout = 5 });
            double waited = -1;
            q[0].TimedOut += w => waited = w;
            server.Advance(4);
            Assert.Equal("waiting", q[0].Status);
            server.Advance(2);
            Assert.Equal("timeout", q[0].Status);
            Assert.True(waited >= 5, $"waited {waited}");
            Assert.True(!q[0].Channel.IsSubscribed || q[0].Channel.Client.Channel(Channel) == null, "unsubscribed");
        }

        [Test]
        static void FillAfterRunsOnTheHostTimer()
        {
            var (server, q) = Setup(new double[] { 0, 0 }, new HibiraQueueOptions { Size = 3, MinSize = 2, FillAfter = 10, Timeout = double.PositiveInfinity });
            server.Advance(5);
            Assert.Equal("waiting", q[0].Status);
            for (var i = 0; i < 6; i++) server.Advance(1);
            Assert.Equal("matched", q[0].Status);
            Assert.Equal("matched", q[1].Status);
            Assert.Equal(2, q[1].Match.Room.Capacity);
        }

        [Test]
        static void LeaveStopsWaiting()
        {
            var (server, q) = Setup(new double[] { 0, 0 }, new HibiraQueueOptions { Size = 3, Timeout = double.PositiveInfinity });
            q[1].Leave();
            server.Settle();
            Assert.Equal("left", q[1].Status);
            Assert.Equal(1, q[0].Count);
        }
    }
}
