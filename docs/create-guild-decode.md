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

- **Functional today:** the DB guild is created and shown via the login `0x203F` push (`.guild create` says
  "Relog to load the guild window"). So create-guild *works*, just not the retail no-relog path.
- **Implemented this session:** parse the request form byte (was dropped).
- **Follow-up (needs the 225-byte tail decoded + a server-side trailer spec):** emit `DispatchGuildCreated`
  (0x2044) immediately so the window opens without a relog, and fan out the `0x003c` guild-chat channel subs.
  Header + member record are decoded byte-exact; only the renown/trailer tail remains. See #<guild-ticket>.
