# Moving from Photon PUN 2

This guide is for Unity teams with a game on Photon PUN 2 who are thinking
about moving its networking to Hibira with this package. Photon's own
documentation says PUN 2 is in maintenance mode and points new projects to
Fusion or Quantum, so a PUN 2 game will have to move somewhere eventually.

We want to be clear about fit first. **Hibira suits turn-based, casual and
party games. It does not suit real-time action games or games that need tick
sync.** If your game is one of the second kind, Photon Fusion or Quantum, or a
dedicated game server, will serve you better than we can.

## Is your game a fit?

| Good fit | Not a fit |
|---|---|
| Card, board, puzzle and word games | Shooters, fighting games, racing |
| Quizzes, trivia, party and drawing games | Physics that has to stay in sync between players |
| Turn-based strategy and auto-battlers | Tick-based simulation, lockstep or rollback netcode |
| Casual co-op where a few updates per second per player are enough | Client-side prediction with server reconciliation or lag compensation |
| Lobbies, matchmaking and chat in front of a separate game server | More than 10 updates per second from one player |
| WebGL games, and games where browser and Unity players share a room | Rooms with more than 100 players |

Why the line falls there:

- **Transport.** Hibira speaks the Pusher Channels protocol over WebSocket,
  which runs on TCP. There is no UDP and no unreliable channel, so a lost
  packet delays the messages behind it.
- **No tick and no simulation.** The server relays events between players. It
  does not run game logic, keep a game clock or own any game state.
- **Limits.** Client events are limited to 10 per second per connection. An
  event can be up to 4 KB on the Free plan, 10 KB on Hobby and Starter and
  more on higher plans ([pricing](https://hibira.io/pricing)). A presence
  channel (a room or a queue) holds up to 100 members.
- **Region.** Our servers run in Hong Kong today, which keeps round trips
  short for players in East and Southeast Asia. Players further away will see
  higher latency.
- **Trust.** Rooms have a host, but the host is one of the players' machines.
  It is not an authoritative server. If cheating would cost you money, keep
  the rules that matter on your own backend.

What you get in return: one hosted WebSocket service that WebGL builds can
reach directly, rooms that browser players (with
[@hibira-io/lobby](https://github.com/hibira-io/lobby-js)) and Godot players
(with the [Godot addon](https://github.com/hibira-io/godot)) can join too, the
standard Pusher server libraries for your backend, and billing in which a
client event counts as one message however many players receive it.

## Concepts side by side

| PUN 2 | This package |
|---|---|
| `PhotonServerSettings` App Id | The app key from the Hibira portal. The app secret stays on your backend. |
| `PhotonNetwork.ConnectUsingSettings()` | `new HibiraClient(key)` and `Connect()`, or the **Hibira Connection** component |
| `PhotonNetwork.NickName`, `AuthValues` | Your auth endpoint signs `user_id` and `user_info` (name, avatar, rating) |
| Lobby, `OnRoomListUpdate` | `listRooms()` on your backend, which reads the HTTP API, served to the game as a list |
| `JoinRandomRoom(expectedProperties, expectedMaxPlayers)`, `JoinRandomOrCreateRoom` | `HibiraQueue.Join` with `Size` and criteria signed into the ticket |
| `CreateRoom(name, new RoomOptions { MaxPlayers = 4 })`, `JoinOrCreateRoom` | `HibiraRoom.Join(client, "presence-room-" + name, new HibiraRoomOptions { Capacity = 4 })` |
| `OnJoinedRoom` | `room.Joined` |
| `OnJoinRoomFailed` (room full) | `room.JoinFailed` (your auth endpoint refused) or `room.Full` |
| `OnPlayerEnteredRoom`, `OnPlayerLeftRoom` | `room.MembersChanged(players, joined, left)` |
| `PhotonNetwork.MasterClient`, `OnMasterClientSwitched` | `room.Host`, `room.HostChanged`. The host is the player who joined first, and when the host leaves the next player takes over. There is no `SetMasterClient`. |
| `Room.SetCustomProperties` | `room.SetSettings(...)` (host only), `room.SettingsChanged` |
| A ready flag in `Player.CustomProperties` | `room.SetReady(true)`, `HibiraPlayer.Ready`, `room.AllReady` |
| `photonView.RPC("Move", RpcTarget.Others, x, y)`, `RaiseEvent` | `room.Channel.Trigger("client-move", data)` and `room.Channel.Bind("client-move", ...)` |
| `RpcTarget.All` | Trigger, then apply the move locally too. Client events are not echoed back to the sender. |
| `RpcTarget.AllBuffered`, buffered events | No buffering. The host sends late joiners a snapshot when `MembersChanged` reports a join (rooms already do this for settings, ready flags and phase). |
| `PhotonNetwork.Instantiate`, `PhotonView`, `OnPhotonSerializeView` | No equivalent. Send a spawn event yourself, and send state when it changes, a few times per second at most. |
| `PhotonNetwork.CloseConnection` | `room.Kick(userId)`. To keep a player out for good, refuse them in the auth endpoint and call `terminate_connections` from the HTTP API. |
| `Room.IsOpen`, `Room.IsVisible` | Your backend decides: refuse the subscription in the auth endpoint, and leave the room out of your list. |
| Interest groups | Separate channels |
| Photon webhooks | Hibira webhooks (`channel_occupied`, `member_added`, `member_removed`, ...) to your backend |
| Best region | One region today (Hong Kong) |

## Step by step

### 1. Create the app

In the [Hibira portal](https://hibira.io), create an app and copy its key
from **Overview → Credentials**. Under **Settings**, turn on **client events**
(rooms, queues and your game events all use them). For WebGL builds, add the
page's origin, for example your itch.io page, to **Allowed origins**.

### 2. Add an auth endpoint

PUN authenticates players against Photon's servers. With Hibira, your
backend signs each room and queue subscription with the app secret, which
never ships in the game. With Node:

```js
import Pusher from 'pusher';
import { authorizeQueue, authorizeRoom, RoomFullError } from '@hibira-io/lobby/server';

const hibira = new Pusher({ appId, key, secret, host: 'ws.hibira.io', useTLS: true });

app.post('/hibira/auth', async (req, res) => {
  const player = await playerFromSession(req); // your login, not the form fields
  const base = {
    socketId: req.body.socket_id,
    channel: req.body.channel_name,
    userId: player.id,
    userInfo: { name: player.name },
  };
  try {
    const auth = base.channel.startsWith('presence-queue-')
      ? await authorizeQueue(hibira, { ...base, rating: player.rating, criteria: { mode: player.mode } })
      : await authorizeRoom(hibira, { ...base, capacity: 4 });
    res.json(auth);
  } catch (err) {
    res.status(err instanceof RoomFullError ? 403 : 500).json({ error: err.message });
  }
});
```

Laravel, Rails and other backends can use their usual Pusher broadcasting
setup. They return `joined_at` (and, for queues, `rating` and `criteria`) in
the presence `user_info`. See the
[lobby-js README](https://github.com/hibira-io/lobby-js#server).

### 3. Connect

```csharp
// PUN 2
PhotonNetwork.NickName = playerName;
PhotonNetwork.ConnectUsingSettings();

// Hibira
var hibira = new HibiraClient(new HibiraOptions
{
    AppKey = "your-app-key",
    AuthEndpoint = "https://your-backend.example/hibira/auth",
});
hibira.Connect();
```

The player's name and rating come from your backend, not from the client.

### 4. Matchmaking

```csharp
// PUN 2
PhotonNetwork.JoinRandomOrCreateRoom(
    expectedCustomRoomProperties: new Hashtable { ["mode"] = "duo" },
    expectedMaxPlayers: 2,
    roomOptions: new RoomOptions { MaxPlayers = 2, CustomRoomPropertiesForLobby = new[] { "mode" } });

// Hibira: one queue per mode, rating range widening while players wait
var queue = HibiraQueue.Join(hibira, "presence-queue-duo", new HibiraQueueOptions
{
    Size = 2,
    RatingRange = 100,
    WidenBy = 50,
    WidenEvery = 5,
    Timeout = 60,
});
queue.Matched += match => OnJoinedMatch(match.Room);
queue.TimedOut += _ => ShowNoMatch();
```

PUN's random join drops a player into any open room that matches the filter.
The queue holds players until a group fits, so everyone starts together in a
fresh room. If you want PUN's behaviour exactly, keep a room list on your
backend (`listRooms()`) and pick an open room there.

Keep every client on the same queue options, because the player hosting the
queue applies its own. If a matched player closes the game at the wrong
moment, the room starts one short: compare `match.Room.Players` with
`match.Players`, and send everyone back to the queue if someone does not
arrive within a few seconds.

### 5. Room callbacks

```csharp
// PUN 2 (MonoBehaviourPunCallbacks)
public override void OnJoinedRoom() => ShowRoom();
public override void OnPlayerEnteredRoom(Player p) => Refresh();
public override void OnPlayerLeftRoom(Player p) => Refresh();
public override void OnMasterClientSwitched(Player host) => Refresh();

// Hibira
room.Joined += (players, host) => ShowRoom();
room.MembersChanged += (players, joined, left) => Refresh();
room.HostChanged += (host, previous) => Refresh();
```

Handlers run on Unity's main thread, as PUN callbacks do.

### 6. RPCs and events

```csharp
// PUN 2
photonView.RPC(nameof(PlayCard), RpcTarget.All, cardId);

[PunRPC]
void PlayCard(int cardId) { /* ... */ }

// Hibira
void Play(int cardId)
{
    room.Channel.Trigger("client-play-card", new Dictionary<string, object> { ["card"] = cardId });
    PlayCard(room.MyId, cardId); // not echoed back, so apply your own move here
}

room.Channel.Bind("client-play-card", e => PlayCard(e.UserId, Convert.ToInt32(e["card"])));
```

`e.UserId` is set by the server, so a player cannot send moves in someone
else's name. Events from one player arrive in the order they were sent.

### 7. Instantiate and serialized views

There is no `PhotonNetwork.Instantiate` or `PhotonView`. In a turn-based
game you usually don't need them: send the move, and every client updates
its own copy of the board. When something does move continuously, such as a
cursor or a token being dragged, send its position when it changes and
interpolate on the other side, at no more than a few updates per second. A
late joiner gets the current state from the host as a snapshot.

### 8. Try it

Run two Editor instances (or a build and the Editor), or a WebGL build next
to a browser page using `@hibira-io/lobby`. The **Lobby** sample in this
package plays a room end to end.

## Things that behave differently

- **No echo.** Your own client events don't come back to you, so apply your
  own moves locally.
- **Reconnects.** The client reconnects and subscribes again by itself. Your
  auth endpoint signs a new `joined_at`, so a player who drops moves to the
  end of the seat order, and if they were the host, the next player takes
  over.
- **The host is a player.** Settings, kicks and start come only from the
  host, and the server stamps every event with its sender. That stops one
  player from pretending to be another. It does not make the host's game
  logic trustworthy.
- **Limits are per connection.** Batch updates instead of sending one event
  per change. The queue already splits large announcements into batches.

If your game fits, we would like to hear how the move goes:
write to us from the support page in the Hibira portal.
