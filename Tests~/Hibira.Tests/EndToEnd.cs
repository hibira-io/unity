using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    /// <summary>
    /// The C# half of Tests~/e2e/e2e.mjs: hosts a room on a real server with the
    /// ClientWebSocket transport, and plays it with an @hibira-io/lobby player.
    /// </summary>
    static class EndToEnd
    {
        static void Say(string line) => Console.WriteLine(line);

        public static async Task<int> Run()
        {
            var env = Environment.GetEnvironmentVariables();
            string Get(string k, string fallback = null) => env.Contains(k) ? (string)env[k] : fallback;
            var client = new HibiraClient(new HibiraOptions
            {
                AppKey = Get("E2E_KEY"),
                Host = Get("E2E_HOST", "127.0.0.1"),
                Port = int.Parse(Get("E2E_PORT", "6001")),
                UseTls = Get("E2E_TLS") == "1",
                AuthEndpoint = Get("E2E_AUTH_URL"),
                AuthParams = new Dictionary<string, string> { ["user_id"] = "u-unity", ["name"] = "Uma" },
                ManualTick = true,
            });
            var errors = new List<string>();
            client.Error += (code, message) => errors.Add($"{code} {message}");
            client.Connected += id => Say($"connected {id}");
            client.Connect();

            var room = HibiraRoom.Join(client, Get("E2E_CHANNEL"));
            room.JoinFailed += (status, message) => errors.Add($"join failed {status} {message}");
            room.Joined += (players, host) => Say("JOINED");
            var gotChat = false;
            var tap = (HibiraEvent)null;
            var jsLeft = false;
            var sentOurs = false;
            room.MessageReceived += m =>
            {
                if (!m.Mine && m.Text == "hi from js" && m.FromName == "Bea") gotChat = true;
            };
            room.Channel.Bind("client-game-tap", e => tap = e);
            room.MembersChanged += (players, joined, left) =>
            {
                if (left?.Id == "u-js") jsLeft = true;
            };

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                client.Tick();
                if (errors.Count > 0) break;
                var js = room.Players.FirstOrDefault(p => p.Id == "u-js");
                if (!sentOurs && js != null && js.Ready && gotChat)
                {
                    if (!room.IsHost) errors.Add("the Unity player should host (joined first)");
                    else
                    {
                        Say($"players: {string.Join(", ", room.Players)}");
                        room.SetSettings(new Dictionary<string, object> { ["map"] = "desert" });
                        room.Send("hi from unity");
                        room.SetReady(true);
                        if (!room.Start(new Dictionary<string, object> { ["seed"] = 42 }, requireAllReady: true))
                            errors.Add("Start refused: not all ready");
                        sentOurs = true;
                        Say("started");
                    }
                }
                if (tap != null && jsLeft) break;
                await Task.Delay(10);
            }
            if (tap == null) errors.Add("no client-game-tap from js");
            else if (tap.UserId != "u-js" || Json.ToDouble(tap["ms"]) != 123) errors.Add($"bad tap {tap}");
            else Say($"tap from {tap.UserId}: {tap.DataJson}");
            if (!jsLeft) errors.Add("js never left");
            client.Dispose();
            foreach (var e in errors) Say($"FAIL {e}");
            if (errors.Count == 0) Say("c# side ok");
            return errors.Count == 0 ? 0 : 1;
        }
    }
}
