using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    static class ClientTests
    {
        sealed class Harness
        {
            public double Now;
            public readonly List<FakeTransport> Sockets = new List<FakeTransport>();
            public readonly HibiraClient Client;
            public FakeTransport Socket => Sockets.Last();

            public Harness(Action<HibiraOptions> configure = null)
            {
                var options = new HibiraOptions
                {
                    AppKey = "app key",
                    ManualTick = true,
                    TransportFactory = () =>
                    {
                        var t = new FakeTransport();
                        Sockets.Add(t);
                        return t;
                    },
                };
                configure?.Invoke(options);
                Client = new HibiraClient(options);
                Client.Clock = () => Now;
            }

            public void Establish(string socketId = "123.456", int activityTimeout = 120)
            {
                Socket.Incoming.Enqueue(TransportEvent.Opened());
                Socket.Push(new Dictionary<string, object>
                {
                    ["event"] = "pusher:connection_established",
                    ["data"] = Json.Serialize(new Dictionary<string, object> { ["socket_id"] = socketId, ["activity_timeout"] = activityTimeout }),
                });
                Client.Tick();
            }

            public void Connect()
            {
                Client.Connect();
                Establish();
            }

            public void Tick(double seconds = 0)
            {
                Now += seconds;
                Client.Tick();
            }
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> m, string key) => (Dictionary<string, object>)m[key];

        [Test]
        static void OpensTheAppUrl()
        {
            var h = new Harness();
            h.Client.Connect();
            Assert.Equal("wss://ws.hibira.io/app/app%20key?protocol=7&client=unity&version=0.1.0", h.Socket.Url);
            Assert.Equal(HibiraConnectionState.Connecting, h.Client.State);

            var local = new Harness(o =>
            {
                o.Host = "127.0.0.1";
                o.Port = 6001;
                o.UseTls = false;
            });
            local.Client.Connect();
            Assert.Equal("ws://127.0.0.1:6001/app/app%20key?protocol=7&client=unity&version=0.1.0", local.Socket.Url);
        }

        [Test]
        static void ConnectNeedsAnAppKey()
        {
            var h = new Harness(o => o.AppKey = "");
            Assert.Throws<InvalidOperationException>(() => h.Client.Connect());
        }

        [Test]
        static void ConnectsAndSubscribesToPublicChannels()
        {
            var h = new Harness();
            string connectedWith = null;
            h.Client.Connected += id => connectedWith = id;
            var scores = h.Client.Subscribe("scores");
            h.Connect();
            Assert.Equal("123.456", connectedWith);
            Assert.Equal("123.456", h.Client.SocketId);
            Assert.Equal(HibiraConnectionState.Connected, h.Client.State);
            var sub = h.Socket.SentEvents("pusher:subscribe").Single();
            Assert.Equal("scores", (string)Obj(sub, "data")["channel"]);

            var subscribed = false;
            scores.Subscribed += _ => subscribed = true;
            h.Socket.Push("{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"scores\",\"data\":\"{}\"}");
            HibiraEvent got = null;
            scores.Bind("updated", e => got = e);
            HibiraEvent any = null;
            h.Client.EventReceived += e => any = e;
            h.Socket.Push("{\"event\":\"updated\",\"channel\":\"scores\",\"data\":\"{\\\"top\\\":[1,2]}\"}");
            h.Tick();
            Assert.True(subscribed && scores.IsSubscribed, "subscribed");
            Assert.Equal("updated", got.Name);
            Assert.Equal("{\"top\":[1,2]}", got.DataJson);
            Assert.Equal(2, ((List<object>)got["top"]).Count);
            Assert.True(got.UserId == null);
            Assert.True(ReferenceEquals(got, any), "client-wide event");
        }

        [Test]
        static void KeepsPlainStringPayloads()
        {
            var h = new Harness();
            var ch = h.Client.Subscribe("news");
            h.Connect();
            HibiraEvent got = null;
            ch.Bind("text", e => got = e);
            h.Socket.Push("{\"event\":\"text\",\"channel\":\"news\",\"data\":\"hello\"}");
            h.Tick();
            Assert.Equal("hello", (string)got.Data);
            Assert.Equal("\"hello\"", got.DataJson);
        }

        [Test]
        static void SubscribingWhileConnectedSendsAtOnce()
        {
            var h = new Harness();
            h.Connect();
            h.Client.Subscribe("later");
            Assert.Equal(1, h.Socket.SentEvents("pusher:subscribe").Count);
            Assert.True(ReferenceEquals(h.Client.Subscribe("later"), h.Client.Channel("later")), "same subscription");
        }

        [Test]
        static void SignsPrivateChannelsWithTheAuthorizer()
        {
            var pending = new TaskCompletionSource<HibiraAuth>();
            string askedSocket = null, askedChannel = null;
            var h = new Harness(o => o.Authorizer = (socketId, channel) =>
            {
                askedSocket = socketId;
                askedChannel = channel;
                return pending.Task;
            });
            h.Client.Subscribe("private-orders");
            h.Connect();
            Assert.Equal("123.456", askedSocket);
            Assert.Equal("private-orders", askedChannel);
            Assert.Equal(0, h.Socket.SentEvents("pusher:subscribe").Count);
            // The authorizer may finish on another thread; the subscribe goes out on the next tick.
            Task.Run(() => pending.SetResult(new HibiraAuth("key:signature"))).Wait();
            Assert.Equal(0, h.Socket.SentEvents("pusher:subscribe").Count);
            h.Tick();
            var data = Obj(h.Socket.SentEvents("pusher:subscribe").Single(), "data");
            Assert.Equal("private-orders", (string)data["channel"]);
            Assert.Equal("key:signature", (string)data["auth"]);
            Assert.True(!data.ContainsKey("channel_data"), "no channel_data on private channels");
        }

        [Test]
        static void ReportsRefusedSubscriptions()
        {
            var h = new Harness(o => o.Authorizer = (s, c) =>
                c == "presence-full" ? throw new HibiraAuthException(403, "room is full") : Task.FromResult<HibiraAuth>(null));
            var errors = new List<(int, string)>();
            h.Client.Subscribe("presence-full").SubscriptionError += (status, message) => errors.Add((status, message));
            h.Client.Subscribe("private-nope").SubscriptionError += (status, message) => errors.Add((status, message));
            h.Client.Subscribe("private-encrypted-x").SubscriptionError += (status, message) => errors.Add((status, message));
            h.Connect();
            h.Tick();
            Assert.True(errors.Contains((403, "room is full")), "403");
            Assert.True(errors.Contains((0, "auth refused")), "null auth");
            Assert.True(errors.Contains((0, "encrypted channels are not supported")), "encrypted");
            Assert.Equal(0, h.Socket.SentEvents("pusher:subscribe").Count);
        }

        [Test]
        static void PrivateChannelsWithoutAuthFail()
        {
            var h = new Harness();
            string error = null;
            h.Client.Subscribe("private-x").SubscriptionError += (_, message) => error = message;
            h.Connect();
            h.Tick();
            Assert.Equal("set AuthEndpoint or Authorizer to use private-x", error);
        }

        [Test]
        static void DropsAuthThatArrivesAfterAReconnect()
        {
            var pending = new TaskCompletionSource<HibiraAuth>();
            var h = new Harness(o => o.Authorizer = (s, c) => pending.Task);
            h.Client.Subscribe("private-a");
            h.Connect();
            var first = h.Socket;
            first.ServerClose(4200, "reconnect");
            h.Tick();
            pending.SetResult(new HibiraAuth("late"));
            h.Tick();
            Assert.Equal(0, first.SentEvents("pusher:subscribe").Count);
        }

        [Test]
        static void TracksPresenceMembers()
        {
            var channelData = "{\"user_id\":\"u1\",\"user_info\":{\"name\":\"Ann\"}}";
            var h = new Harness(o => o.Authorizer = (s, c) => Task.FromResult(new HibiraAuth("k:s", channelData)));
            var party = h.Client.Subscribe("presence-party");
            h.Connect();
            h.Tick();
            var data = Obj(h.Socket.SentEvents("pusher:subscribe").Single(), "data");
            Assert.Equal(channelData, (string)data["channel_data"]);
            Assert.Equal("u1", party.MyId);

            object subscribedWith = null;
            party.Subscribed += d => subscribedWith = d;
            var added = new List<string>();
            var removed = new List<(string, object)>();
            party.MemberAdded += (id, _) => added.Add(id);
            party.MemberRemoved += (id, info) => removed.Add((id, info));
            var bound = new List<string>();
            party.Bind("pusher:member_added", e => bound.Add("added " + e["id"]));
            party.Bind("pusher:subscription_succeeded", e => bound.Add("subscribed"));

            h.Socket.Push("{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"presence-party\",\"data\":\"{\\\"presence\\\":{\\\"ids\\\":[\\\"u1\\\",\\\"u2\\\"],\\\"hash\\\":{\\\"u1\\\":{\\\"name\\\":\\\"Ann\\\"},\\\"u2\\\":{\\\"name\\\":\\\"Bo\\\"}},\\\"count\\\":2}}\"}");
            h.Socket.Push("{\"event\":\"pusher_internal:member_added\",\"channel\":\"presence-party\",\"data\":\"{\\\"user_id\\\":\\\"u3\\\",\\\"user_info\\\":{\\\"name\\\":\\\"Cy\\\"}}\"}");
            h.Socket.Push("{\"event\":\"pusher_internal:member_added\",\"channel\":\"presence-party\",\"data\":\"{\\\"user_id\\\":\\\"u3\\\",\\\"user_info\\\":{\\\"name\\\":\\\"Cy\\\"}}\"}");
            h.Socket.Push("{\"event\":\"pusher_internal:member_removed\",\"channel\":\"presence-party\",\"data\":\"{\\\"user_id\\\":\\\"u2\\\"}\"}");
            h.Socket.Push("{\"event\":\"pusher_internal:member_removed\",\"channel\":\"presence-party\",\"data\":\"{\\\"user_id\\\":\\\"nobody\\\"}\"}");
            h.Tick();
            Assert.True(subscribedWith is Dictionary<string, object> p && (long)p["count"] == 2, "presence hash");
            Assert.Equal("u3", string.Join(",", added));
            Assert.Equal(1, removed.Count);
            Assert.Equal("u2", removed[0].Item1);
            Assert.Equal("Bo", (string)((Dictionary<string, object>)removed[0].Item2)["name"]);
            Assert.Equal("u1,u3", string.Join(",", party.Members.Keys.OrderBy(k => k)));
            Assert.Equal("subscribed,added u3", string.Join(",", bound));
        }

        [Test]
        static void SendsAndReceivesClientEvents()
        {
            var h = new Harness(o => o.Authorizer = (s, c) => Task.FromResult(new HibiraAuth("k:s", "{\"user_id\":\"me\"}")));
            var room = h.Client.Subscribe("presence-room");
            h.Connect();
            h.Tick();
            Assert.True(!room.Trigger("client-move", 1), "not subscribed yet");
            h.Socket.Push("{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"presence-room\",\"data\":\"{\\\"presence\\\":{\\\"hash\\\":{\\\"me\\\":null}}}\"}");
            h.Tick();
            Assert.True(room.Trigger("client-move", new Dictionary<string, object> { ["x"] = 3 }), "sent");
            var sent = h.Socket.SentEvents("client-move").Single();
            Assert.Equal("presence-room", (string)sent["channel"]);
            Assert.Equal(3L, (long)Obj(sent, "data")["x"]);

            Assert.Throws<ArgumentException>(() => h.Client.Trigger("presence-room", "move", 1));
            Assert.Throws<ArgumentException>(() => h.Client.Trigger("public", "client-move", 1));

            HibiraEvent got = null;
            room.Bind("client-move", e => got = e);
            h.Socket.Push("{\"event\":\"client-move\",\"channel\":\"presence-room\",\"user_id\":\"them\",\"data\":{\"x\":4}}");
            h.Tick();
            Assert.Equal("them", got.UserId);
            Assert.Equal(4L, (long)got["x"]);
            Assert.Equal("{\"x\":4}", got.DataJson);
        }

        [Test]
        static void AnswersPings()
        {
            var h = new Harness();
            h.Connect();
            h.Socket.Push("{\"event\":\"pusher:ping\",\"data\":{}}");
            h.Tick();
            Assert.Equal(1, h.Socket.SentEvents("pusher:pong").Count);
        }

        [Test]
        static void PingsWhenIdleAndReconnectsWithoutAPong()
        {
            var h = new Harness(o => o.PongTimeout = 10);
            var ch = h.Client.Subscribe("scores");
            h.Client.Connect();
            h.Establish(activityTimeout: 30); // the server's timeout is shorter than ours
            h.Tick(29);
            Assert.Equal(0, h.Socket.SentEvents("pusher:ping").Count);
            h.Tick(2);
            Assert.Equal(1, h.Socket.SentEvents("pusher:ping").Count);
            var disconnects = new List<(int, bool)>();
            h.Client.Disconnected += (code, _, retry) => disconnects.Add((code, retry));
            h.Tick(11);
            Assert.Equal(4201, h.Sockets[0].CloseCode);
            Assert.True(h.Sockets[0].Disposed, "old socket disposed");
            Assert.Equal((4201, true), disconnects.Single());
            Assert.Equal(HibiraConnectionState.Reconnecting, h.Client.State);
            Assert.True(!ch.IsSubscribed, "channels reset");
            h.Tick(); // 42xx: reconnect at once
            Assert.Equal(2, h.Sockets.Count);
            h.Establish("9.9");
            Assert.Equal(1, h.Socket.SentEvents("pusher:subscribe").Count);
            Assert.Equal("9.9", h.Client.SocketId);
        }

        [Test]
        static void TrafficCountsAsActivity()
        {
            var h = new Harness();
            h.Connect();
            h.Tick(100);
            h.Socket.Push("{\"event\":\"x\",\"channel\":\"y\",\"data\":\"{}\"}");
            h.Tick();
            h.Tick(100);
            Assert.Equal(0, h.Socket.SentEvents("pusher:ping").Count);
            h.Tick(21);
            Assert.Equal(1, h.Socket.SentEvents("pusher:ping").Count);
        }

        [Test]
        static void BacksOffAfterDroppedConnections()
        {
            var h = new Harness();
            h.Connect();
            h.Socket.ServerClose(1006, "gone");
            h.Tick();
            Assert.Equal(1, h.Sockets.Count);
            h.Tick(1.01); // first retry within 0.5-1 s
            Assert.Equal(2, h.Sockets.Count);
            h.Socket.ServerClose(1006, "gone");
            h.Tick();
            h.Tick(0.4);
            Assert.Equal(2, h.Sockets.Count);
            h.Tick(1.7); // second retry within 1-2 s
            Assert.Equal(3, h.Sockets.Count);
        }

        [Test]
        static void StopsOnFatalCloseCodes()
        {
            var h = new Harness();
            h.Connect();
            bool? retry = null;
            h.Client.Disconnected += (_, __, r) => retry = r;
            h.Socket.ServerClose(4001, "app does not exist");
            h.Tick();
            h.Tick(60);
            Assert.Equal(false, retry);
            Assert.Equal(1, h.Sockets.Count);
            Assert.Equal(HibiraConnectionState.Disconnected, h.Client.State);
        }

        [Test]
        static void DisconnectStopsReconnecting()
        {
            var h = new Harness();
            h.Connect();
            var states = new List<HibiraConnectionState>();
            h.Client.StateChanged += s => states.Add(s);
            bool? retry = null;
            h.Client.Disconnected += (_, __, r) => retry = r;
            h.Client.Disconnect();
            Assert.Equal(1000, h.Socket.CloseCode);
            Assert.Equal(false, retry);
            Assert.Equal(HibiraConnectionState.Disconnected, h.Client.State);
            h.Tick(60);
            Assert.Equal(1, h.Sockets.Count);
            h.Client.Connect();
            Assert.Equal(2, h.Sockets.Count);
        }

        [Test]
        static void UnsubscribeTellsTheServer()
        {
            var h = new Harness();
            var ch = h.Client.Subscribe("scores");
            h.Connect();
            h.Socket.Push("{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"scores\",\"data\":\"{}\"}");
            h.Tick();
            ch.Unsubscribe();
            Assert.Equal("scores", (string)Obj(h.Socket.SentEvents("pusher:unsubscribe").Single(), "data")["channel"]);
            Assert.True(h.Client.Channel("scores") == null, "removed");
        }

        [Test]
        static void ReportsServerErrors()
        {
            var h = new Harness();
            h.Connect();
            (int, string) error = default;
            h.Client.Error += (code, message) => error = (code, message);
            h.Socket.Push("{\"event\":\"pusher:error\",\"data\":{\"code\":4301,\"message\":\"rate limited\"}}");
            h.Tick();
            Assert.Equal((4301, "rate limited"), error);
        }

        [Test]
        static async Task PostsToTheAuthEndpoint()
        {
            var listener = new HttpListener();
            var port = new Random().Next(20000, 40000);
            var prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(prefix);
            listener.Start();
            string body = null, authorization = null, contentType = null;
            var serve = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var ctx = await listener.GetContextAsync();
                    using (var reader = new System.IO.StreamReader(ctx.Request.InputStream)) body = await reader.ReadToEndAsync();
                    authorization = ctx.Request.Headers["Authorization"];
                    contentType = ctx.Request.ContentType;
                    var refuse = body.Contains("presence-full");
                    ctx.Response.StatusCode = refuse ? 403 : 200;
                    var reply = Encoding.UTF8.GetBytes(refuse ? "{\"error\":\"room is full\"}" : "{\"auth\":\"k:sig\",\"channel_data\":\"{\\\"user_id\\\":\\\"7\\\"}\"}");
                    await ctx.Response.OutputStream.WriteAsync(reply, 0, reply.Length);
                    ctx.Response.Close();
                }
            });
            try
            {
                var h = new Harness(o =>
                {
                    o.AuthEndpoint = prefix + "auth";
                    o.AuthHeaders["Authorization"] = "Bearer t0k";
                    o.AuthParams["player"] = "a b&c";
                });
                var room = h.Client.Subscribe("presence-room-1");
                h.Connect();
                for (var i = 0; i < 200 && h.Socket.SentEvents("pusher:subscribe").Count == 0; i++)
                {
                    await Task.Delay(10);
                    h.Tick();
                }
                Assert.Equal("socket_id=123.456&channel_name=presence-room-1&player=a%20b%26c", body);
                Assert.Equal("Bearer t0k", authorization);
                Assert.True(contentType.StartsWith("application/x-www-form-urlencoded"), contentType);
                Assert.Equal("k:sig", (string)Obj(h.Socket.SentEvents("pusher:subscribe").Single(), "data")["auth"]);
                Assert.Equal("7", room.MyId);

                (int, string) error = default;
                h.Client.Subscribe("presence-full").SubscriptionError += (s, m) => error = (s, m);
                for (var i = 0; i < 200 && error.Item1 == 0; i++)
                {
                    await Task.Delay(10);
                    h.Tick();
                }
                Assert.Equal((403, "room is full"), error);
                await serve;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
