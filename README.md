# Hibira Realtime for Unity

[![ci](https://github.com/hibira-io/unity/actions/workflows/ci.yml/badge.svg)](https://github.com/hibira-io/unity/actions/workflows/ci.yml)

A Pusher Channels protocol client for Unity, in plain C#, for
[Hibira](https://hibira.io) and other Pusher-compatible servers. It runs in
the Editor, on desktop, Android and iOS, and in **WebGL builds**, where it uses
the browser's WebSocket through a small `.jslib`.

- Public, private and presence channels
- Client events (`client-*`) between players, with the sender's `user_id`
  on presence channels
- Presence members, joins and leaves
- Reconnects with backoff and subscribes again; answers and sends pings
- Events on Unity's main thread, so you can touch GameObjects in handlers
- `HibiraRoom`: game rooms with seats, a host, ready flags, settings, kick,
  start, and chat with typing indicators. It speaks the same events as
  [@hibira-io/lobby](https://github.com/hibira-io/lobby-js) and the
  [Godot addon](https://github.com/hibira-io/godot), so Unity, Godot and
  browser players can share a room.
- No dependencies on other packages

Requires Unity 2021.3 or later.

## Install

In Unity, open **Window → Package Manager**, click **+**, choose **Add
package from git URL…**, and enter:

```
https://github.com/hibira-io/unity.git
```

To pin a version, add the tag: `https://github.com/hibira-io/unity.git#v0.1.0`.
Or add it to `Packages/manifest.json` yourself:

```json
{
  "dependencies": {
    "io.hibira.realtime": "https://github.com/hibira-io/unity.git#v0.1.0"
  }
}
```

The package includes a **Lobby** sample (Package Manager → Hibira Realtime →
Samples → Import).

## Connect and listen

```csharp
using Hibira;
using UnityEngine;

public class Scores : MonoBehaviour
{
    HibiraClient hibira;

    void Start()
    {
        hibira = new HibiraClient("your-app-key"); // from the Hibira portal; never the secret
        hibira.Connected += socketId => Debug.Log($"connected {socketId}");
        hibira.Connect();

        var scores = hibira.Subscribe("scores");
        scores.Bind("updated", e => Debug.Log(e.DataJson));
    }

    void OnDestroy() => hibira.Dispose();
}
```

The default host is `ws.hibira.io` on port 443 with TLS. For another
Pusher-compatible server, pass `HibiraOptions` with `Host`, `Port` and
`UseTls`.

You can also add the **Hibira Connection** component to a GameObject, fill in
the app key in the Inspector, and use `GetComponent<HibiraConnection>().Client`
from your scripts.

Event data arrives decoded: `e.Data` is a `Dictionary<string, object>`,
`List<object>`, `long`, `double`, `string`, `bool` or `null`, and `e["field"]`
reads a field. To read it into your own class, use `JsonUtility` through
`DataAs<T>()`:

```csharp
[System.Serializable]
public class Score { public string player; public int points; }

scores.Bind("updated", e =>
{
    var score = e.DataAs<Score>();
    Debug.Log($"{score.player}: {score.points}");
});
```

## Private and presence channels

Your backend signs these subscriptions with the app secret, exactly as for
pusher-js, so any backend that already works with pusher-js or Laravel Echo
works here. Point the client at it:

```csharp
hibira = new HibiraClient(new HibiraOptions
{
    AppKey = "your-app-key",
    AuthEndpoint = "https://your-game.example/hibira/auth",
    AuthHeaders = { ["Authorization"] = $"Bearer {sessionToken}" },
    AuthParams = { ["player"] = playerId }, // extra form fields, if your endpoint wants them
});
```

The endpoint gets a form POST with `socket_id` and `channel_name` and returns
`{"auth": "...", "channel_data": "..."}`. To sign some other way, set
`Authorizer`:

```csharp
options.Authorizer = async (socketId, channelName) =>
{
    var res = await myApi.SignChannel(socketId, channelName);
    return new HibiraAuth(res.auth, res.channel_data); // or null to refuse
};
```

Never ship the app secret in a game build: anyone can read it out of the
build.

```csharp
var party = hibira.Subscribe("presence-party-7");
party.Subscribed += _ => Debug.Log($"members: {party.Members.Count}");
party.MemberAdded += (id, info) => Debug.Log($"{id} joined");
party.MemberRemoved += (id, info) => Debug.Log($"{id} left");
party.SubscriptionError += (status, message) => Debug.Log($"refused: {status} {message}");

// Client events go to the other subscribers, not back to you. Turn on client
// events for the app in the Hibira portal (App → Settings).
party.Trigger("client-emote", new Dictionary<string, object> { ["emote"] = "wave" });
party.Bind("client-emote", e => ShowEmote(e.UserId, (string)e["emote"]));
```

`Trigger` takes dictionaries, lists, numbers and strings, or any
`[Serializable]` class (written with `JsonUtility`).

## Game rooms

```csharp
var room = HibiraRoom.Join(hibira, "presence-room-42", new HibiraRoomOptions { Capacity = 4 });
room.Joined += (players, host) => Render(players);
room.MembersChanged += (players, joined, left) => Render(players);
room.HostChanged += (host, previous) => Debug.Log($"{host.Name} is the host now");
room.SettingsChanged += (settings, by) => ShowSettings(settings);
room.MessageReceived += m => AddChatLine(m.FromName, m.Text);
room.TypingChanged += ids => ShowTyping(ids);
room.Started += (data, by) => StartGame(data);
room.Full += capacity => Debug.Log("This room is full");
room.Kicked += (by, reason) => LeaveScreen();

room.SetReady(true);
room.Send("gl hf");
chatInput.onValueChanged.AddListener(_ => room.SendTyping());

if (room.IsHost)
{
    room.SetSettings(new Dictionary<string, object> { ["map"] = "desert", ["rounds"] = 3 });
    room.Start(new Dictionary<string, object> { ["seed"] = 1234 }, requireAllReady: true);
}

room.Players;  // seat order: Id, Name, Info, Ready, Host, Me
room.Leave();
```

Seats and the host come from the `joined_at` your auth endpoint signs into
`user_info`; `authorizeRoom()` in `@hibira-io/lobby/server` does that for Node,
and the [lobby-js README](https://github.com/hibira-io/lobby-js#server) shows
Laravel. The rules (who may send what, what happens when the host leaves, the
capacity race) are the same as in
[@hibira-io/lobby](https://github.com/hibira-io/lobby-js#readme). Game events
of your own go on the same channel: `room.Channel.Trigger("client-move", …)`.

## WebGL

WebGL builds use the browser's WebSocket and `fetch` (through
UnityWebRequest), so two things change:

- Add the page's origin (for example `https://your-game.example` or your
  itch.io page) to the app's **allowed origins** in the Hibira portal.
- The auth endpoint needs CORS for that origin, or serve the game from the
  backend's own origin.

The browser answers the server's pings by itself, so a player who switches
to another tab stays connected while Unity's frames are paused.

## Threads and ticking

Everything runs on Unity's main thread: a hidden `Hibira` object ticks every
client once a frame. To tick a client yourself (in a test, say), set
`ManualTick = true` and call `Tick()`.

## API

`HibiraClient`

| | |
|---|---|
| `Connect()`, `Disconnect()`, `Dispose()` | open; close and stop reconnecting; close for good |
| `Subscribe(name)`, `Unsubscribe(name)`, `Channel(name)` | channels; subscriptions survive reconnects |
| `Trigger(channel, event, data)` | send a client event |
| `State`, `SocketId` | connection state and socket id |
| `StateChanged`, `Connected`, `Disconnected(code, reason, willReconnect)`, `Error(code, message)` | connection events |
| `EventReceived` | every event on every channel |

`HibiraChannel`: `Bind(event, handler)`, `Unbind(event, handler)`,
`Trigger(event, data)`, `Unsubscribe()`, `IsSubscribed`, `Members`, `MyId`,
and the events `Subscribed`, `SubscriptionError`, `EventReceived`,
`MemberAdded`, `MemberRemoved`, `CountChanged`.

Not supported yet: end-to-end encrypted channels (`private-encrypted-`) and
user sign-in (`pusher:signin`).

## Limits

Hibira's limits apply as for any client: client events are limited to 10 per
second per connection, an event's data to 4 KB on the Free plan (10 KB on
Hobby and Starter, more on higher plans; see
[pricing](https://hibira.io/pricing)), and presence channels hold up to 100
members.

## Development

The C# in `Runtime/` builds outside Unity too, which is how it is tested:

```sh
dotnet run --project Tests~/Hibira.Tests             # protocol and room tests
dotnet build Tests~/UnityCompile                     # compiles against Unity 2021.3's assemblies
dotnet build Tests~/UnityCompile -p:WebGL=true       # ... as a WebGL player build
(cd Tests~/webgl && npm ci && npm test)              # the WebGL .jslib, in Node
python3 Tests~/check-meta.py                         # every asset has a .meta
```

`Tests~/e2e` plays a room between this client and an `@hibira-io/lobby`
player on a real server with client events turned on:

```sh
cd Tests~/e2e && npm ci
E2E_HOST=127.0.0.1 E2E_PORT=6001 E2E_TLS=0 E2E_APP_ID=... E2E_KEY=... E2E_SECRET=... npm test
```

## License

[MIT](LICENSE).
