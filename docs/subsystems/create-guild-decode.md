# Create-guild flow — decoded from `DemoL80_mage-demonologist_login_create-guild.pcapng`

The guild-create RPC on the CsPlayerAgent connection (`GuildService.HandleCreateGuild`).

## Request — `CreateGuild` (0x2021), C→S

```
[u16 len]"faolan"   guild name (s16)
u8  form = 0x06     government/guild-form the client chose
u16 0
```
Body from the capture: `00 06 "faolan" 06 00 00`. `HandleCreateGuild` reads the name via `ReadString()` and
then the trailing `form` byte (`0x06`) — the guild form the player selected in the registrar window — leaving
the final `00 00` unread.

## Response — `DispatchGuildCreated` (0x2044), S→C (not `0x203F`)

Retail's immediate create response is **`DispatchGuildCreated` = 0x2044**, not the `GuildFullUpdate` (0x203F).
338-byte body, fully mapped:

```
[creator member record]        # EXACTLY `GuildMemberRecordCodec` format, 81 bytes:
  03 03 · [charId u32] · [name s16] · [rank u8=4 leader] · [online u8=1] · [playfield u32] ·
  [level u8] · [class u32] · [u32 0] · [lastSeen u32] · [48 zero bytes]
[guild header]                 # identical to the 0x203F body head EXCEPT the post-cash byte:
  u8 version=3 · u32 guildId · u8 govForm=1 · u64 cash=0 · u8 form(=request's 0x06; 0x203F hardcodes 0x03) ·
  u32 0 · u8 0 · [name s16 "Faolan"] · [desc s16 ""] · [motd s16 ""]
[tail, 225 bytes]              # the renown block in its create-time form (see below):
  u32 0 · u32 0x3F1 · <~177 bytes, mostly 0 with feat numbers 0xFC4/0x410/0x816/0xBEA> ·
  40-byte trailer: "7111"(0x37313131) · 0x0007A128 · 0 · 0x064AB178 · 0 · 0x09D5D338 · 0 · 0 · 0x01A93458
```

The trailer holds capture-specific values (creation timestamps / ids), so a new guild generates different bytes
there — the byte-exact goal is the header/member-record (deterministic); the tail is structural.

## The refusals — `0x45`–`0x49`, and they carry no body at all

A create that the server will not honour is answered by one of five methods in the same block, and every one of
`0x45`–`0x48` is a **no-arg dispatch**: the whole message is the method id, because the client already knows
which name it just submitted. `annotated_6` catches the taken-name case — `CreateGuild "Faolan"` at 20:21:52
answered in the same second by `0x2047` with an EMPTY body and nothing else.

A no-arg method has no argument signature for the client's string catalog to record, so two of the five have no
name in that catalog. They are recovered from the code instead: the dispatcher hands each one to
an interface vtable slot, and `CSClientIncoming_c`'s override of that slot fetches the **LDB category-100 token**
it displays.

| method | interface slot | LDB 100 token | name |
|---:|---|---|---|
| `0x45` | `+0x118` | `NonAlphanumericGuildName` | `DispatchGuildNonAlphanumericName` — *named in the catalog, the control* |
| `0x46` | `+0x11C` | `WrongLengthGuildName` | **`DispatchGuildWrongLengthName`** |
| `0x47` | `+0x120` | `TakenGuildName` | **`DispatchGuildTakenName`** |
| `0x48` | `+0x124` | `BadGuildName` | `DispatchGuildBadName` — *named in the catalog, the second control* |
| `0x49` | — | — | `DispatchGuildNameWaitTime` (carries a body) |

**Both ends of the block land on the catalog's own names**, which is what makes the two in the middle
trustworthy rather than plausible. `HandleCreateGuild` sends `0x47` on a taken name; the other three have
no server-side rule to trigger them. Open work tracked in Gitea #737.

## Full server sequence (frames 362→383)

1. C→S `0x2021 CreateGuild` `[name][form]`
2. S→C `0x2000` (GameServer state) + **`0x2044 DispatchGuildCreated`** (the guild window opens immediately — no relog)
3. S→C `0x003c` — the `~Guild` chat-channel subscription (the guild chat tab), then a re-fan of the standing channels
4. C→S `0x202f` (member-joined ack, len 0) then more `0x003c`
5. `0x2085`/`0x2082` follow-ups

## What we build

- `HandleCreateGuild` emits `DispatchGuildCreated` (0x2044) — the retail no-relog create response. Built
  byte-exact against this capture (`GuildPacketTests.GuildCreatedBody_Matches_CapturedBytes`, 338 bytes) by
  `GuildCreatedCodec`: member record, header and tail are all constructed. The login `0x203F` push also runs.
- The guild leader's wire rank is **4** (this capture's member record) — `Character.GuildLeaderRank`.
- The request's `form` byte is parsed.
- **The tail** is the same `CoreGuild_c::Read` renown block as the 0x203F (FUN_00565900, case 1→3), written in its
  create-time form: `Flag=0` (the full update uses 1) and an empty StatIds list (so 225 = 277 − 52), zeroed renown,
  the feat defaults `List2 = 0x410/0x816/0xBEA`, and create-time constants in `Counters64C[0..2]`/`Counters64D`.
  `GuildHeaders.ForGuildCreate` carries `GuildRenownBlock.ForGuildCreate()`
  (byte-exact — `GuildRenownBlockCodecTests.ForGuildCreate_EncodesToTheCapturedCreateTail`).
- **The `0x003c` guild chat-channel subscription (step 3 above) is sent** — `RefreshGuildChannel` on the chat
  tier, driven from create / invite-accept / leave / kick, plus the login burst for a character already guilded.
  There is exactly one channel and it is `~Guild`; see below.

## The guild chat channel — `~Guild`, and there is no officer one

The subscription is an ordinary `ChannelSubscription` (0x003C) whose **id IS the guild id**, which is what makes
it constructible: the same word the guild's own header carries.

```
03 <guildId u32> 0006 "~Guild" 00008004 0000
```

Two captures state it independently, from the two directions a client can acquire a guild:

| capture | guild | the guild's own header | its `~Guild` channel |
|---|---|---|---|
| `DemoL80_…_login_create-guild` fr366/369 | created in-session | `0x2044` … `03 00086c47` | `03 00086c47 "~Guild"` |
| `Hoxxling_…_login` fr114 | already a member | `0x203F` `03 0007585b` | `03 0007585b "~Guild"` |

The flag is **`0x00008004`**, not the `0x00008044` the standing channels (`~Global` / `~LFG` / `~Playfield` /
`~NewbieHelp`) carry — the same word the captured `~Team` subscription uses, so the per-membership channels
share a flag distinct from the standing ones.
[V2: DemoL80_mage-demonologist_login_create-guild.tshark.json]

**There is no officer channel.** Sweeping all 120 dumps for a 0x003C/0x003D naming a guild, officer or team
channel returns `~Guild` and `~Team` and nothing else, and the client binary carries no `~`-prefixed channel
name at all (they are server-supplied). The decisive case is the create capture: DemoL80 is by construction the
guild leader, and retail subscribes them to exactly one channel. So "guild + officer chat channels" is not how
this build works.

**Leaving is uncaptured.** No session in the corpus ever leaves a guild, so the `0x003D` we send on a
leave/kick is that message — the one the corpus does show retiring a `~Team` when the group dissolves and a
`~Playfield` on a zone change — applied to this channel. The guild case itself is inferred.
[V0: retail's 0x003D on a guild leave/kick]
