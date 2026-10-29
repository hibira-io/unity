# Changelog

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
