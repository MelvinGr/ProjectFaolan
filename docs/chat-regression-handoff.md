# Handoff: fix the in-client chat-channels regression

Paste the block below into a fresh session to resume. Full prior investigation is in
memory `protocol_chat_connection_regression` (and `TODO.md` → "Chat channels broken in-client").

---

```
Fix the in-client chat-channels regression in Project Faolan.

SYMPTOM: in-world only the local "say" tab exists — Global/LFG/Trial channels are
missing. The client logs "Connecting to CommunityServer" → "Authenticating" → ~20s
timeout → "HandleDisconnect redir=0". The AgentServer (chat, port 7012) gets ZERO TCP
accepts — the client connects to every other server but never the chat server.

This is a REGRESSION, already bisected. Read memory protocol_chat_connection_regression
first — it has the full prior investigation. Do NOT re-derive these (already proven):
- Root cause class: the world-entry construct-don't-replay rewrite. Chat WORKS at commit
  3d20d34 (raw-blob replay), BROKEN at e58b06c→HEAD (construct path). The client never
  finishes world-load, so never dials the chat server.
- Ruled out: character/id (0x02000000 works at 3d20d34, fails at HEAD), the PlayerAgent
  0x1A07 AgentServer advertisement (byte-identical/correct), the post-ready burst (chat
  connects BEFORE ReadyForPlayScreen), the NPC death-loop (fixed), environment/hosts/LocalConfig.
- Already tried, did NOT fix: restoring CoreClaim 0x06EC1255 (committed 4ebce3f, keep it),
  removing 2 spurious N3ToClientQuit despawns, dropping the 0x2076 manifest.
- Opcode-COUNT diffing is too noisy (different impls + capture-session gameplay) — don't.

THE APPROACH TO TRY: byte-level, not opcode-level.
1. Get a loopback packet capture of a WORKING raw-blob login (checkout 3d20d34) and of a
   BROKEN HEAD login, and byte-diff the GameServer world-build stream from the 0x201c
   handshake through the spawn-check (0x2020→0x2008) stream — find what the client receives
   in the working run that it doesn't (or receives differently) at HEAD, right up to where
   the working client opens the chat socket.
   OR
2. Re-extract the Tortage Beach (4010) world burst WITHOUT over-filtering and byte-compare to
   the raw blob. Suspect over-filters: commits ea84a38 ("reject gameplay-polluted windows")
   and 13bd715 ("drop transient rows"). IirRecordCodec is round-trip lossless, so the only
   loss is whatever extraction filtered out.

TEST METHOD (autonomous — you can drive the client yourself):
- Run server from Faolan/bin/Debug: `dotnet Faolan.dll` (cwd MUST be bin/Debug so
  Program.cs's ../../../faolan.json resolves).
- Drive the client headless:
  ./Faolan.ClientLauncher/bin/Debug/Faolan.ClientLauncher --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <charName>
  (account `faolan` password is "-" = accepts any; --universeaddr is required.)
- Chat is FIXED iff the server log shows: `AgentServerListener[0] Client connected`.
- Test on HEAD (NPC aggro is gated, so no death-loop to re-enter the world and confound).
- GitVersion fails on detached HEAD — checkout test commits onto a temp branch (git checkout -B tmp <sha>).
- Note: char id is irrelevant (it's the commit, not the char). DB-in-git: `git checkout -- faolan.db`
  before switching commits; restore to `development` + delete temp branches when done.

Goal: the client opens the chat connection and channels appear in-world.
```
