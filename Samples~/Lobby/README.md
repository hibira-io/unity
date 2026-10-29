# Lobby sample

A room with seats, a host, ready flags, kick, chat with typing indicators,
and start, drawn with IMGUI so there is no scene to set up.

1. Add `LobbySample` to an empty GameObject.
2. Set **App Key** (the key, not the secret) for an app with client events
   turned on.
3. Point **Backend Url** at a backend that signs room subscriptions. The
   sample uses the same rooms and auth endpoint as
   [nextjs-lobby](https://github.com/hibira-io/examples/tree/main/nextjs-lobby):
   start it with `npm run dev` and keep the default `http://localhost:3000`.
4. Press **Play**, make a room, and join it from a browser at
   `http://localhost:3000/?room=CODE`. Unity and browser players share the
   room.

The sample sends the player's id and name to the auth endpoint as form
fields, as nextjs-lobby expects. A real game sends its own session instead
and lets the backend decide who the player is.
