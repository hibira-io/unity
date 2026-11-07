# Asset Store listing kit

Everything needed to list **Hibira Realtime** on the Unity Asset Store as a
free package. Folders ending in `~` are skipped by Unity, so none of this ends
up in a project that installs the package.

- `export.sh`: copies the package into a Unity project's `Assets/Hibira/Realtime`
  folder in the classic Asset Store layout (Runtime, Samples, Documentation).
- `images/`: key images and screenshots at the sizes the Publisher Portal asks for.
- The listing text below, ready to paste.

## Submitting

The Asset Store only accepts uploads made from Unity **2022.3 or newer**. The
package itself still supports 2021.3, so list that as the lowest version.

1. Make a new, empty Unity 2022.3 project (on Apple silicon, install Rosetta
   first: `softwareupdate --install-rosetta`; the 2022.3 Package Manager is an
   Intel binary).
2. Run `AssetStore~/export.sh /path/to/that/project` from this repository.
3. Open the project and check the Console shows no compile errors.
4. Install **Asset Store Publishing Tools**: "Add to My Assets" on its Asset
   Store page, then Package Manager → My Assets → Download → Import. It is not
   in the Unity Registry. (The git URL from Unity's GitHub repo also works, but
   its icons are in Git LFS, so without `git lfs` the Validator window breaks.)
5. Open **Tools → Asset Store → Uploader**, sign in, pick the draft package,
   choose `Assets/Hibira`, run the validator, and upload.
6. In the [Publisher Portal](https://publisher.unity.com/packages), fill in the
   draft with the text and images below, set the price to Free, and submit.
   The publisher profile needs a Website (https://hibira.io) or the submit is
   refused.

## Later releases

`.github/workflows/asset-store.yml` builds the upload in Unity 2022.3 on every
`v*` tag (it fails if anything in `Assets/Hibira` doesn't compile) and attaches
`Hibira-Realtime-<version>.unitypackage` to the GitHub release. Then:

1. In any Unity 2022.3 project with Asset Store Publishing Tools, open
   **Tools → Asset Store → Uploader**, pick the package, choose
   **From .unitypackage**, select the file from the release, validate and upload.
2. Submit in the Publisher Portal. Unity reviews every version by hand.

Uploading itself stays manual: Asset Store Publishing Tools has no public API
for it, only the Editor window.

The workflow needs three repository secrets, set once (Settings → Secrets and
variables → Actions):

- `UNITY_EMAIL` and `UNITY_PASSWORD`: the Unity account that holds the license.
- `UNITY_LICENSE`: the contents of the license file for that account. Follow
  GameCI's [activation guide](https://game.ci/docs/github/activation) for a
  Personal license.

Without them the workflow skips the build with a warning instead of failing.

The Asset Store also offers "hybrid" (UPM-format) uploads, but that support is
still marked experimental, so this kit uses the classic `Assets/` layout.

## Images

| File | Use | Size |
| --- | --- | --- |
| `images/icon-160x160.png` | Icon | 160×160, no text |
| `images/card-420x280.png` | Card | 420×280 |
| `images/cover-1950x1300.jpg` | Cover | 1950×1300 |
| `images/social-1200x630.jpg` | Social media | 1200×630, no text |
| `images/screenshot-0-cover.jpg` | Screenshot 1 | 1920×1080 |
| `images/screenshot-1-connect.png` | Screenshot 2 | 1920×1080 |
| `images/screenshot-2-rooms.png` | Screenshot 3 | 1920×1080 |

None of them use the Unity logo. A screenshot of the Lobby sample running in
Play mode would make a good fourth screenshot once one is taken in the Editor.

## Listing text

**Package name:** Hibira Realtime

**Category:** Tools / Network

**Price:** Free

**Supported Unity versions:** 2021.3 and later

**Keywords:** multiplayer, realtime, websocket, lobby, matchmaking, presence, chat, webgl, pusher, networking

**Summary (short description):**
Realtime channels, presence, game rooms and matchmaking for Unity, WebGL included. No game server to run.

**Description:**

Hibira Realtime adds live multiplayer features to your game without a game server to host. It is a plain C# client for Hibira, a hosted realtime service, and for any server that speaks the Pusher Channels protocol.

Good for turn-based, casual and party games, lobbies, chat, live scores and anything else where a message only needs to arrive in well under a second. It is not meant for fast action games that need tick sync.

**Features**
• Public, private and presence channels, with who's online, joins and leaves
• Client events between players, with the sender's user ID on presence channels
• Game rooms: seats, a host, ready flags, room settings, kick, start, and chat with typing indicators
• Matchmaking queue: matches players by rating and criteria, times out, and moves the match into a new room
• Works in WebGL builds through a small .jslib, and in the Editor, on desktop, Android and iOS
• Events arrive on Unity's main thread, so handlers can touch GameObjects
• Reconnects with backoff and subscribes again
• Unity, Godot and browser players can share the same room
• A Lobby sample to start from
• No dependencies on other packages

**Getting started**
1. Create a free app at hibira.io (100 connections, no card).
2. Add the Hibira Connection component to a GameObject and enter your app key, or create a HibiraClient in code.
3. Subscribe to a channel and bind to events. The included documentation covers private and presence channels, rooms and matchmaking.

A Hibira account is required; the Free plan is enough to build and test. Your backend signs private and presence channels with the app secret, which never goes into the game build.

Documentation and source: https://github.com/hibira-io/unity

**Technical details:**
• Unity 2021.3 or later
• Platforms: Windows, macOS, Linux, Android, iOS, WebGL
• Plain C#, one assembly (Hibira.Realtime); no native plugins apart from a WebGL .jslib
• Source code included
• Documentation: Documentation/README.md in the package, and online at https://github.com/hibira-io/unity
• License: MIT. The Asset Store EULA applies to the Asset Store copy, and the same code is also available under MIT on GitHub.

If the package is already installed from git through the Package Manager, remove that copy before importing this one.

---

## 中文摘要

這裡是把 Hibira Realtime 上架 Unity Asset Store（免費）所需的全部材料：`export.sh` 把套件複製成 Asset Store 要求的 `Assets/Hibira/Realtime` 結構；`images/` 有 icon、card、cover、social 圖與三張截圖，尺寸都符合 Publisher Portal 的要求；上方是可直接貼上的英文上架文案。流程：開一個 Unity 2021.3 LTS 空專案，執行 `export.sh`，在 Editor 用 Asset Store Publishing Tools 上傳 `Assets/Hibira`，再到 Publisher Portal 填文案、上傳圖片並送審。上傳與送審需要在 Unity Editor 與 Publisher Portal 登入，只能由帳號擁有者操作。
