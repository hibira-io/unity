using System;
using System.Collections.Generic;
using System.Linq;

namespace Hibira.Tests
{
    static class RoomTests
    {
        const string Channel = "presence-room-1";

        static (FakeServer server, HibiraRoom[] rooms) Setup(int players, HibiraRoomOptions options = null)
        {
            var server = new FakeServer();
            var rooms = new List<HibiraRoom>();
            var names = new[] { "Ann", "Bo", "Cy", "Di" };
            for (var i = 0; i < players; i++)
            {
                var client = server.NewClient($"u{i + 1}", names[i]);
                client.Connect();
                rooms.Add(HibiraRoom.Join(client, Channel, options));
                // Join one at a time so joined_at orders them.
                server.Settle();
            }
            return (server, rooms.ToArray());
        }

        static string Ids(IEnumerable<HibiraPlayer> players) => string.Join(",", players.Select(p => p.Id));

        [Test]
        static void RejectsNonPresenceChannels()
        {
            var client = new FakeServer().NewClient("u1", "Ann");
            Assert.Throws<ArgumentException>(() => HibiraRoom.Join(client, "private-room"));
            Assert.Throws<ArgumentException>(() => HibiraRoom.Join(client, "presence-large-room"));
        }

        [Test]
        static void OrdersPlayersByJoinTimeAndPicksTheFirstAsHost()
        {
            var server = new FakeServer();
            var a = server.NewClient("u1", "Ann");
            a.Connect();
            var room = HibiraRoom.Join(a, Channel);
            IReadOnlyList<HibiraPlayer> joinedWith = null;
            HibiraPlayer hostWith = null;
            room.Joined += (players, host) =>
            {
                joinedWith = players;
                hostWith = host;
            };
            server.Settle();
            Assert.True(room.IsJoined, "joined");
            Assert.Equal("u1", Ids(joinedWith));
            Assert.Equal("Ann", hostWith.Name);
            Assert.True(room.IsHost && room.Me.Me && room.Me.Host, "host is me");

            var b = server.NewClient("u2", "Bo");
            b.Connect();
            var roomB = HibiraRoom.Join(b, Channel);
            HibiraPlayer joinedPlayer = null;
            room.MembersChanged += (players, joined, left) => joinedPlayer = joined ?? joinedPlayer;
            server.Settle();
            Assert.Equal("u1,u2", Ids(room.Players));
            Assert.Equal("u1,u2", Ids(roomB.Players));
            Assert.Equal("u2", joinedPlayer.Id);
            Assert.True(!roomB.IsHost && roomB.Host.Id == "u1", "Ann hosts for Bo");
        }

        [Test]
        static void ShareReadyFlags()
        {
            var (server, rooms) = Setup(2);
            rooms[1].SetReady(true);
            Assert.True(rooms[1].Me.Ready, "own flag applied locally");
            server.Settle();
            Assert.True(rooms[0].Players[1].Ready, "seen by the host");
            Assert.True(!rooms[0].AllReady, "host not ready");
            rooms[0].SetReady();
            server.Settle();
            Assert.True(rooms[0].AllReady && rooms[1].AllReady, "all ready");
        }

        [Test]
        static void OnlyTheHostChangesSettings()
        {
            var (server, rooms) = Setup(2);
            Assert.Throws<InvalidOperationException>(() => rooms[1].SetSettings(new Dictionary<string, object> { ["map"] = "x" }));
            string by = null;
            rooms[1].SettingsChanged += (s, b) => by = b;
            rooms[0].SetSettings(new Dictionary<string, object> { ["map"] = "desert", ["rounds"] = 3 });
            rooms[0].SetSettings(new Dictionary<string, object> { ["rounds"] = 5 });
            server.Settle();
            Assert.Equal("desert", (string)rooms[1].Settings["map"]);
            Assert.Equal(5L, Convert.ToInt64(rooms[1].Settings["rounds"]));
            Assert.Equal("u1", by);

            // A forged settings event from a non-host is ignored.
            var b = server.Clients[1];
            b.Trigger(Channel, HibiraRoom.EventSettings, new Dictionary<string, object> { ["settings"] = new Dictionary<string, object> { ["map"] = "evil" }, ["seq"] = 99 });
            server.Settle();
            Assert.Equal("desert", (string)rooms[0].Settings["map"]);
        }

        [Test]
        static void TheHostSendsNewMembersASnapshot()
        {
            var (server, rooms) = Setup(2, new HibiraRoomOptions { StateDelay = 0.15 });
            rooms[0].SetSettings(new Dictionary<string, object> { ["map"] = "ice" });
            rooms[1].SetReady();
            server.Settle();

            var c = server.NewClient("u3", "Cy");
            c.Connect();
            var roomC = HibiraRoom.Join(c, Channel);
            server.Settle();
            Assert.Equal(0, roomC.Settings.Count);
            server.Advance(0.2);
            Assert.Equal("ice", (string)roomC.Settings["map"]);
            Assert.True(roomC.Players.Single(p => p.Id == "u2").Ready, "ready flags in the snapshot");
            Assert.Equal("waiting", roomC.Phase);
        }

        [Test]
        static void StartReachesEveryone()
        {
            var (server, rooms) = Setup(3);
            Assert.Throws<InvalidOperationException>(() => rooms[1].Start());
            Assert.True(!rooms[0].Start(null, requireAllReady: true), "not everyone is ready");
            var started = new List<string>();
            foreach (var r in rooms) r.Started += (data, by) => started.Add($"{((Dictionary<string, object>)data)["seed"]} by {by}");
            Assert.True(rooms[0].Start(new Dictionary<string, object> { ["seed"] = 1234 }), "started");
            server.Settle();
            Assert.Equal("1234 by u1,1234 by u1,1234 by u1", string.Join(",", started));
            Assert.True(rooms.All(r => r.Phase == "started"), "phase");
        }

        [Test]
        static void KickedPlayersLeave()
        {
            var (server, rooms) = Setup(3);
            Assert.Throws<InvalidOperationException>(() => rooms[0].Kick("u1"));
            (string, object) kicked = default;
            rooms[2].Kicked += (by, reason) => kicked = (by, reason);
            var forgedKick = false;
            rooms[1].Kicked += (_, __) => forgedKick = true;
            // Only the host's kick counts.
            server.Clients[2].Trigger(Channel, HibiraRoom.EventKick, new Dictionary<string, object> { ["user_id"] = "u2" });
            rooms[0].Kick("u3", "afk");
            server.Settle();
            Assert.Equal(("u1", (object)"afk"), kicked);
            Assert.True(!forgedKick, "non-host kick ignored");
            Assert.Equal("u1,u2", Ids(rooms[0].Players));
            Assert.True(server.Clients[2].Channel(Channel) == null, "unsubscribed");
        }

        [Test]
        static void ChatAndTyping()
        {
            var (server, rooms) = Setup(2, new HibiraRoomOptions { TypingTimeout = 4 });
            var typing = new List<string>();
            rooms[0].TypingChanged += ids => typing.Add(string.Join("+", ids));
            rooms[1].SendTyping();
            rooms[1].SendTyping(); // throttled
            server.Settle();
            Assert.Equal("u2", string.Join(",", typing));
            Assert.Equal(1, server.Clients[1].Channel(Channel) != null ? 1 : 0);

            HibiraChatMessage got = null;
            rooms[0].MessageReceived += m => got = m;
            var mine = rooms[1].Send("gl hf", new Dictionary<string, object> { ["emoji"] = "🎮" });
            Assert.True(mine.Mine && rooms[1].Messages.Count == 1, "own message stored");
            server.Settle();
            Assert.Equal("gl hf", got.Text);
            Assert.Equal("Bo", got.FromName);
            Assert.Equal(mine.Id, got.Id);
            Assert.Equal("🎮", (string)((Dictionary<string, object>)got.Extra)["emoji"]);
            Assert.True(!got.Mine, "not mine");
            Assert.Equal("u2,", string.Join(",", typing)); // chat clears typing

            rooms[1].SendTyping(); // still throttled (2 s)
            server.Advance(2.1);
            rooms[1].SendTyping();
            server.Settle();
            Assert.Equal("u2,,u2", string.Join(",", typing));
            server.Advance(4.1);
            Assert.Equal("u2,,u2,", string.Join(",", typing)); // expired
        }

        [Test]
        static void KeepsTheLastMessages()
        {
            var (server, rooms) = Setup(2, new HibiraRoomOptions { MaxMessages = 3 });
            for (var i = 0; i < 5; i++) rooms[1].Send($"m{i}");
            server.Settle();
            Assert.Equal("m2,m3,m4", string.Join(",", rooms[0].Messages.Select(m => m.Text)));
        }

        [Test]
        static void TheLastToJoinAFullRoomLeaves()
        {
            var (server, rooms) = Setup(2, new HibiraRoomOptions { Capacity = 2 });
            var c = server.NewClient("u3", "Cy");
            c.Connect();
            var roomC = HibiraRoom.Join(c, Channel, new HibiraRoomOptions { Capacity = 2 });
            int? full = null;
            roomC.Full += cap => full = cap;
            server.Settle();
            Assert.Equal(2, full);
            Assert.True(c.Channel(Channel) == null, "left");
            Assert.Equal("u1,u2", Ids(rooms[0].Players));
            Assert.Equal(0, rooms[0].Waiting.Count);
        }

        [Test]
        static void TheNextMemberTakesOverAsHost()
        {
            var (server, rooms) = Setup(3);
            rooms[0].SetSettings(new Dictionary<string, object> { ["map"] = "ice" });
            server.Settle();
            (string host, string previous) changed = default;
            rooms[1].HostChanged += (host, previous) => changed = (host.Id, previous);
            HibiraPlayer left = null;
            rooms[1].MembersChanged += (_, __, l) => left = l ?? left;
            rooms[0].Leave();
            server.Settle();
            Assert.Equal(("u2", "u1"), changed);
            Assert.True(left.Id == "u1" && left.Host, "left player was host");
            Assert.True(rooms[1].IsHost, "Bo hosts");
            Assert.Equal("ice", (string)rooms[1].Settings["map"]);
            rooms[1].SetSettings(new Dictionary<string, object> { ["map"] = "lava" });
            server.Settle();
            Assert.Equal("lava", (string)rooms[2].Settings["map"]);
        }

        [Test]
        static void ActionsNeedAJoinedRoom()
        {
            var client = new FakeServer().NewClient("u1", "Ann");
            var room = HibiraRoom.Join(client, Channel);
            Assert.Throws<InvalidOperationException>(() => room.SetReady());
            Assert.Throws<InvalidOperationException>(() => room.Send("hi"));
            Assert.True(room.Host == null && room.Me == null && room.Players.Count == 0, "empty before joining");
        }

        [Test]
        static void ReportsARefusedJoin()
        {
            var server = new FakeServer();
            var client = new HibiraClient(new HibiraOptions
            {
                AppKey = "key",
                ManualTick = true,
                TransportFactory = () => new FakeTransport { Server = server },
                Authorizer = (s, c) => throw new HibiraAuthException(403, "room is full"),
            });
            server.Clients.Add(client);
            client.Connect();
            var room = HibiraRoom.Join(client, Channel);
            (int, string) failed = default;
            room.JoinFailed += (status, message) => failed = (status, message);
            server.Settle();
            Assert.Equal((403, "room is full"), failed);
        }

        [Test]
        static void TracksAChannelSubscribedEarlier()
        {
            var (server, rooms) = Setup(1);
            var client = server.Clients[0];
            var again = new HibiraRoom(client.Channel(Channel));
            Assert.True(again.IsJoined && again.IsHost, "picks up the joined channel");
        }
    }
}
