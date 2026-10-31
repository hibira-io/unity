using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hibira.Tests
{
    /// <summary>
    /// The C# half of Tests~/e2e/queue-e2e.mjs: waits in a queue on a real
    /// server next to @hibira-io/lobby players, hosts the matching (it joins
    /// first), and meets its match in the match room.
    /// </summary>
    static class EndToEndQueue
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
                AuthParams = new Dictionary<string, string> { ["user_id"] = "u-unity", ["name"] = "Uma", ["rating"] = "1000" },
                ManualTick = true,
            });
            var errors = new List<string>();
            client.Error += (code, message) => errors.Add($"{code} {message}");
            client.Connect();

            var queue = HibiraQueue.Join(client, Get("E2E_CHANNEL"), new HibiraQueueOptions { Size = 2, RatingRange = 100, Timeout = 20 });
            queue.JoinFailed += (status, message) => errors.Add($"join failed {status} {message}");
            queue.Joined += (position, count) => Say("JOINED");
            queue.TimedOut += _ => errors.Add("timed out in the queue");
            HibiraMatchFound match = null;
            queue.Matched += m =>
            {
                match = m;
                Say($"matched {string.Join(",", m.Players.Select(p => p.Id))} in {m.Channel}");
            };

            var met = false;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && errors.Count == 0)
            {
                client.Tick();
                if (match?.Room != null && match.Room.Players.Any(p => p.Id == "u-js-near"))
                {
                    met = true;
                    Say("MET");
                    break;
                }
                await Task.Delay(10);
            }
            if (match == null) errors.Add("no match");
            else
            {
                var ids = string.Join(",", match.Players.Select(p => p.Id).OrderBy(x => x));
                if (ids != "u-js-near,u-unity") errors.Add($"matched with {ids}, want u-js-near");
                if (!met) errors.Add("never met the js player in the match room");
            }
            // Give the JS side a moment to see us in the room before leaving.
            var linger = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < linger)
            {
                client.Tick();
                await Task.Delay(10);
            }
            client.Dispose();
            foreach (var e in errors) Say($"FAIL {e}");
            if (errors.Count == 0) Say("c# side ok");
            return errors.Count == 0 ? 0 : 1;
        }
    }
}
