using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Hibira.Samples
{
    /// <summary>
    /// A game room with seats, a host, ready flags, kick, chat with typing
    /// indicators, and start, drawn with IMGUI so it needs no scene setup: add
    /// it to an empty GameObject, fill in the Inspector, and press Play.
    /// </summary>
    /// <remarks>
    /// The rooms are the ones nextjs-lobby in hibira-io/examples uses
    /// (presence-room-ABCD, four seats), and its backend signs them, so a Unity
    /// player and browser players can sit in the same room.
    /// </remarks>
    public class LobbySample : MonoBehaviour
    {
        [Tooltip("Your Hibira app key (never the secret). Turn on client events for the app in the portal.")]
        public string appKey = "";
        public string host = "ws.hibira.io";
        public int port = 443;
        public bool useTls = true;
        [Tooltip("The backend that signs room subscriptions, e.g. nextjs-lobby running locally.")]
        public string backendUrl = "http://localhost:3000";
        public string authPath = "/api/hibira/auth";
        public string playerName = "Unity player";
        public int capacity = 4;

        HibiraClient _client;
        HibiraRoom _room;
        string _playerId;
        string _code = "";
        string _chat = "";
        string _status = "";
        Vector2 _scroll;

        void Start()
        {
            _playerId = "unity-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (string.IsNullOrEmpty(appKey))
            {
                _status = "Set the app key on the LobbySample component.";
                return;
            }
            _client = new HibiraClient(new HibiraOptions
            {
                AppKey = appKey,
                Host = host,
                Port = port,
                UseTls = useTls,
                AuthEndpoint = backendUrl.TrimEnd('/') + authPath,
                // nextjs-lobby's auth endpoint takes the player from the form.
                // A real game sends its own session token instead.
                AuthParams = new Dictionary<string, string> { ["user_id"] = _playerId, ["name"] = playerName },
            });
            _client.StateChanged += s => _status = s.ToString();
            _client.Error += (code, message) => _status = $"Error {code}: {message}";
            _client.Connect();
        }

        void OnDestroy() => _client?.Dispose();

        void JoinRoom(string code)
        {
            _code = code.Trim().ToUpperInvariant();
            _room = HibiraRoom.Join(_client, "presence-room-" + _code, new HibiraRoomOptions { Capacity = capacity });
            _room.Joined += (players, roomHost) => _status = $"In room {_code}";
            _room.JoinFailed += (status, message) =>
            {
                _status = $"Could not join: {message}";
                LeaveRoom();
            };
            _room.Full += _ =>
            {
                _status = "That room is full.";
                _room = null;
            };
            _room.Kicked += (by, reason) =>
            {
                _status = "The host asked you to leave.";
                _room = null;
            };
            _room.HostChanged += (newHost, _) => _status = $"{newHost.Name} is the host now";
            _room.Started += (data, by) => _status = $"The game started: {Json.Serialize(data)}";
        }

        void LeaveRoom()
        {
            _room?.Leave();
            _room = null;
        }

        static string NewCode()
        {
            var rng = new System.Random();
            var sb = new StringBuilder();
            for (var i = 0; i < 4; i++) sb.Append((char)('A' + rng.Next(26)));
            return sb.ToString();
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, Mathf.Min(480, Screen.width - 32), Screen.height - 32));
            GUILayout.Label($"Hibira lobby sample · {_status}");
            if (_client == null || _client.State != HibiraConnectionState.Connected && _room == null)
            {
                GUILayout.EndArea();
                return;
            }
            if (_room == null) DrawRoomPicker();
            else DrawRoom();
            GUILayout.EndArea();
        }

        void DrawRoomPicker()
        {
            GUILayout.Label("Room code (4 letters)");
            _code = GUILayout.TextField(_code, 4);
            GUILayout.BeginHorizontal();
            GUI.enabled = _code.Length == 4;
            if (GUILayout.Button("Join")) JoinRoom(_code);
            GUI.enabled = true;
            if (GUILayout.Button("New room")) JoinRoom(NewCode());
            GUILayout.EndHorizontal();
        }

        void DrawRoom()
        {
            GUILayout.Label($"Room {_code} · {_room.Players.Count}/{capacity} · {_room.Phase}");
            foreach (var p in _room.Players)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{(p.Host ? "★ " : "")}{p.Name}{(p.Me ? " (you)" : "")} {(p.Ready ? "✓ ready" : "")}");
                if (_room.IsHost && !p.Me && GUILayout.Button("Kick", GUILayout.Width(60))) _room.Kick(p.Id, "kicked by host");
                GUILayout.EndHorizontal();
            }
            if (!_room.IsJoined)
            {
                GUILayout.Label("Joining…");
                if (GUILayout.Button("Cancel")) LeaveRoom();
                return;
            }

            GUILayout.BeginHorizontal();
            var ready = _room.Me?.Ready ?? false;
            if (GUILayout.Button(ready ? "Not ready" : "I'm ready")) _room.SetReady(!ready);
            if (_room.IsHost)
            {
                GUI.enabled = _room.AllReady;
                if (GUILayout.Button("Start game"))
                    _room.Start(new Dictionary<string, object> { ["seed"] = UnityEngine.Random.Range(1, 1_000_000) }, requireAllReady: true);
                GUI.enabled = true;
            }
            if (GUILayout.Button("Leave")) LeaveRoom();
            GUILayout.EndHorizontal();
            if (_room == null) return;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(200));
            foreach (var m in _room.Messages) GUILayout.Label($"{m.FromName}: {m.Text}");
            GUILayout.EndScrollView();
            var typing = _room.Typing.Select(id => _room.Players.FirstOrDefault(p => p.Id == id)?.Name ?? id).ToList();
            GUILayout.Label(typing.Count == 0 ? " " : $"{string.Join(", ", typing)} typing…");

            GUILayout.BeginHorizontal();
            var before = _chat;
            _chat = GUILayout.TextField(_chat, 200);
            if (_chat != before && _chat.Length > 0) _room.SendTyping();
            var send = GUILayout.Button("Send", GUILayout.Width(60)) ||
                       (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return);
            if (send && _chat.Trim().Length > 0)
            {
                _room.Send(_chat.Trim());
                _chat = "";
            }
            GUILayout.EndHorizontal();
        }
    }
}
