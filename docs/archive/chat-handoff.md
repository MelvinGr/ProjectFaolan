# Chat broken again — handoff (2026-06-13) — SOLVED (archived)

**RESOLVED 2026-06-13.** Two root causes, both fixed: (1) the client flooded `RequestAggroStatus` because the
server mis-dispatched it, hanging the loading screen (`5c99a6a`, route GameCharAgentProject `0x5B9756FC` by
class id and reply `[0xC350][target][0]`); (2) the DB-container world-entry `0x737A6DF9` snapshot rejected the
player's own FullChar — the snapshot's protobuf root is length-prefixed by a 4-byte BE length the server wrote
wrong on resize (interim verbatim default `5c7f2a8`, real fix `85c2886` = `SetSnapshotBlobLength`; framing in
`docs/archive/db-container-handoff.md`). Chat connects, names render, the bag shows the player's DB items. The
investigation notes below are kept verbatim for the method (live-client log triage past the RDB/resource noise).

---

Paste-ready prompt + state of play for fixing **in-game chat not working after world entry**.

## Symptom
After a successful world entry (beach renders, NPCs/Casilda visible, abilities granted), **chat does not work**
in-client. Exact user-visible symptom still to pin down (no chat window? can't send? channels empty?) — get this
first.

## The one hard signal we have
On the clean-server login (server log = the running `dotnet Faolan.dll` task output, NOT `/tmp/faolan-server.log`
which is a stale process), the client connects to:
- `UniverseAgent` (7010), `PlayerAgent` (7011), `GameServer` (7014), `CSPlayerAgent` (7001, connects then
  **immediately disconnects**)
- **`AgentServer` (7012, chat) gets NO `Client connected` line at all.**

So the chat (AgentServer) connection is never established. Chat connects only AFTER the client finishes "loading".

## What is NOT the cause (already ruled out this session)
- **The `CoreClaimIIR 0x06EC1255` is still sent.** That record (in `SendPostReadyBurst`, `Packets.cs:111` →
  `Faolan/GameServer/Login/PostReadyBurst.cs:26`) is the world-CLAIM signal that lets the client finish loading and
  open the CommunityServer/chat connection — its ABSENCE is the classic "channels-missing" bug. It is present
  (it was in `loopback7`'s download stream, and the call path is intact). So this is a DIFFERENT cause than the
  historical one.
- Snapshot/container mutation breaking the chat gate was the **dead retail CDN** era — superseded once the
  ContentMirror served resources locally (memory `protocol_chat_connection_regression`). Don't re-chase that.

## Likely areas (investigate in order)
1. **Did chat work EARLIER this session, then break? Bisect.** This session changed: the world-entry seeding /
   prune / verbatim-burst fixes, the class-aware `0x2052` ability grants sent during entry, and a full DB re-import
   (`--skip-characters`, which rebuilt items/conversations/shops). The client connected to AgentServer in some
   earlier runs (experiment-server logs) but not the latest clean run. Find the first build/run where chat stopped
   connecting (git is clean at the abilities/world commits; toggle the ability grant + check).
2. **Is the in-game session unstable AFTER entry?** The clean-server log shows a quick disconnect sequence
   (PlayerAgent off, CSPlayerAgent on+off, GameServer left map). If the client drops/crashes shortly after entry it
   never reaches the chat connect. Grab `AgeOfConan.log`
   (`/var/games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log`) right at the point chat should
   connect — look for a crash/assert/`WatchDog`/`stream bad` after the post-ready burst.
3. **Does the client even open a TCP socket to 7012?** Capture a fresh loopback of a chat-broken login
   (`Captures/tshark_dump.sh`), check for any 7012 traffic. If the client never opens 7012, the trigger to connect
   is missing/late; if it opens then drops, the AgentServer handshake is failing.
4. **Compare to a retail chat connection.** Retail dumps have full 7012 traffic
   (`account-stricken997-character-randochar-login-kalantes_chat.tshark.json` etc.): `0x003c` channel subs, `0x0014`
   sender info, `0x0023` system msg — sent DURING entry (before the first move). Diff our AgentServer handshake
   against retail's to see what the client waits for. The full post-ready record stream after CoreClaim (records in
   `PostReadyBurst.cs`) may be incomplete/misframed so the client never signals "ready for chat".

## Files / context
- `Faolan/GameServer/Login/PostReadyBurst.cs` — the post-0x2024 record stream incl. the CoreClaim; the gate to
  "loading done → open chat".
- `Faolan/GameServer/Packets/Packets.cs:111` — where `SendPostReadyBurst` is called.
- `Faolan/AgentServer/AgentServerListener.cs` — the chat server (channel subs `0x003C`, chat `0x0041`, sender info
  `0x0014`, system `0x0023`, local say `0x0022`).
- `docs/chat.md` (protocol), memories `protocol_agentserver_chat`, `protocol_chat_connection_regression`.

## How to run / test
- Server: `cd Faolan/bin/Debug && dotnet Faolan.dll` (cwd MUST be bin/Debug).
- Client: `Faolan.ClientLauncher --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c Darktemplarchar`.
- After a run: `git checkout -- faolan.db`. Loopback capture → `Captures/tshark_dump.sh Captures/<cap>.pcapng`.
- Server-side signal: grep the running server's log for `AgentServer.*Client connected` — its ABSENCE = chat never
  connected.

## Paste-ready prompt
> Fix in-game chat (read `docs/chat-handoff.md` + TODO §2 "Chat broken again"). After world entry the client never
> connects to AgentServer (7012); UniverseAgent/PlayerAgent/GameServer connect, chat doesn't. The `CoreClaimIIR
> 0x06EC1255` (the historical "channels-missing" cause) IS still sent, so it's a different cause. Start by getting
> the exact symptom + an `AgeOfConan.log` and a fresh loopback at the moment chat should connect, then bisect this
> session's changes (ability grants / re-import / world-entry fixes) and diff our AgentServer handshake against the
> retail `…kalantes_chat` capture's 7012 traffic. Don't re-chase the dead-CDN/snapshot-gate theory.
