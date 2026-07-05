# Tools to install (for Melvin) — requested by autonomous sessions

Running list of tooling that would unblock work an agent couldn't do with what's installed.
Delete an entry once it's installed + confirmed working.

## 2026-07-06 (autonomous session)

- **`frida-server` for the Proton/Wine client** — `frida` (Python) + `frida-tools` are on PATH, but
  live-attaching the retail client under Proton needs the injected agent. This session's #234 RE was doable
  **fully offline** with the pre-analyzed Ghidra project (`/home/melvin/Development/Ghidra/Aoc`), so nothing was
  blocked — but the remaining leads that need *runtime* values (e.g. verifying the stock-phrase remoteformat
  `key` resolves the same in a live session, or dumping the cinematic script-object → live-handle map at
  runtime) would want Frida-under-Wine working. See memory `frida_under_wine_client_re`.
  - Nothing here is currently blocking; this is a "would be nice for live-RE" note.

- **Nothing else missing.** Ghidra (`/usr/bin/ghidra`, `ghidra-analyzeHeadless`), Wine, tshark, dotnet 10 all
  present and used this session.

## Non-tool follow-ups noticed (file as Gitea issues, not installs)

- 3 xUnit `[Theory]` tests fail on .NET 10 with `System.OverflowException: Value 'True' of type
  'System.Boolean' does not fit in Byte` (`BehaviorAnimationCodecTests.SpellweaveComponentAdd_MatchesCapture`
  x2, `GuildPacketTests.GuildMemberRecord_Matches_CapturedBytes`). Pre-existing (fail on a clean checkout of
  `development`), unrelated to any feature — an xUnit/runtime bool-InlineData coercion bug. Pin the xUnit
  version or change the bool params to an enum/int.
