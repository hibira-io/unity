# Changelog

## 0.2.0

- `HibiraQueue`: a matchmaking queue on a presence channel, compatible with
  `Queue` in `@hibira-io/lobby` 0.2. It matches players by signed rating
  (with a range that widens while they wait) and criteria, allows a smaller
  match after `FillAfter`, gives up after `Timeout`, and moves matched players
  into a new `HibiraRoom` on `presence-match-<id>`. No server changes needed.
- `HibiraMatch`: the matching rules as plain functions.
- Guide: [Moving from Photon PUN 2](Documentation~/moving-from-photon-pun-2.md).

## 0.1.0

- `HibiraClient`: Pusher Channels protocol 7 client with public, private and
  presence channels, client events, reconnects with backoff, pings, and an
  auth endpoint or custom authorizer.
- WebGL builds use the browser's WebSocket through `HibiraWebSocket.jslib`,
  which also answers server pings while the tab is in the background.
- `HibiraRoom`: game rooms compatible with `@hibira-io/lobby` 0.1 and the
  Godot addon (seats, host, ready flags, settings, kick, start, chat, typing).
- `HibiraConnection` component for setup in the Inspector.
- Lobby sample.
