# Create-guild flow — decoded from `DemoL80_mage-demonologist_login_create-guild.pcapng` (2026-07-05)

The guild-CREATE RPC on the CsPlayerAgent connection, captured for the first time (`HandleCreateGuild` was
previously written blind — "No retail capture of the upload exists").

## Request — `CreateGuild` (0x2021), C→S

```
[u16 len]"faolan"   guild name (s16)
u8  form = 0x06     government/guild-form the client chose  (the current code ignores this)
u16 0
```
Body from the capture: `00 06 "faolan" 06 00 00`. The current `HandleCreateGuild` reads only the name via
`ReadString()` and drops the trailing `06 00 00`; the `0x06` is the guild form the player selected in the
registrar window.

## Response — `DispatchGuildCreated` (0x2044), S→C  (NOT `0x203F`!)

Retail's immediate create response is **`DispatchGuildCreated` = 0x2044** (opcode already named in
`Opcodes.cs`), not the `GuildFullUpdate` (0x203F) the blind impl sends. 338-byte body, fully mapped:

```
[creator member record]        # EXACTLY WriteGuildMemberRecord format, 81 bytes:
  03 03 · [charId u32] · [name s16] · [rank u8=4 leader] · [online u8=1] · [playfield u32] ·
  [level u8] · [class u32] · [u32 0] · [lastSeen u32] · [48 zero bytes]
[guild header]                 # identical to the 0x203F body head EXCEPT the post-cash byte:
  u8 version=3 · u32 guildId · u8 govForm=1 · u64 cash=0 · u8 form(=request's 0x06; 0x203F hardcodes 0x03) ·
  u32 0 · u8 0 · [name s16 "Faolan"] · [desc s16 ""] · [motd s16 ""]
[tail, 225 bytes]              # renown-ish + trailer, DISTINCT from the 277-byte GuildRenownBlock:
  u32 0 · u32 0x3F1 · <~177 bytes, mostly 0 with feat numbers 0xFC4/0x410/0x816/0xBEA> ·
  40-byte trailer: "7111"(0x37313131) · 0x0007A128 · 0 · 0x064AB178 · 0 · 0x09D5D338 · 0 · 0 · 0x01A93458
```

The trailer holds capture-specific values (creation timestamps / ids), so a NEW guild generates different bytes
there — the byte-exact goal is the header/member-record (deterministic); the tail is structural.

## Full server sequence (frames 362→383)

1. C→S `0x2021 CreateGuild` `[name][form]`
2. S→C `0x2000` (GameServer state) + **`0x2044 DispatchGuildCreated`** (the guild window opens immediately — no relog)
3. S→C several `0x003c` — guild + officer CHAT-CHANNEL subscriptions (the guild chat tabs)
4. C→S `0x202f` (member-joined ack, len 0) then more `0x003c`
5. `0x2085`/`0x2082` follow-ups

## Status

- **Implemented (#306, `45905d5d`):** `HandleCreateGuild` now emits `DispatchGuildCreated` (0x2044) — the
  retail no-relog create response — instead of only the `0x203F` full update. Built byte-exact against this
  capture (`GuildPacketTests.GuildCreatedBody_Matches_CapturedBytes`, 338 bytes) by `WriteGuildCreatedBody`:
  member record + header are CONSTRUCTED; the 225-byte tail is emitted verbatim as a valid create-time block
  (this capture is itself a fresh guild, so its feat numbers are create-time defaults — only the 40-byte
  trailer holds cosmetic capture timestamps). The login `0x203F` push still runs, so relog remains a fallback.
- **The golden corrected a blind guess:** the guild LEADER's wire rank is **4** (this capture's member record),
  not the 6 the server hardcoded. `Character.GuildLeaderRank` fixed — no rank-comparison logic depended on it.
- Also parses the request's `form` byte (previously dropped).
- **Remaining follow-ups:** decode the 225-byte tail field-by-field (so a guild's real renown/creation values are
  constructed rather than templated), and fan out the `0x003c` guild + officer chat-CHANNEL subs (step 3 below)
  so the guild chat tabs appear. Neither blocks the window opening. Needs an in-client no-relog confirmation.
