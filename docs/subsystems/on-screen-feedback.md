# On-screen feedback / notification overlay (FormatFeedbackIIR)

How AoC shows the **top-of-screen notification banners and feedback lines** — the overlay messages that are
**NOT** the chat window (zone-name reveal, "you picked up X", buff worn off, attribute increased, PvP trackers).

Decoded 2026-06-14 from the gameplay / Has-inventory captures. Byte-exact builder `FormatFeedbackCodec`
(`Faolan.Core.Network`), golden test `FormatFeedbackCodecTests`, live send `GameServerListener.SendFeedback`
(the standalone `.feedback` GM test command was retired in the GM-command cleanup; the live send now fires
from the level-up attribute path).

## The mechanism

The message is a **`FormatFeedbackIIR`** (IIR sub-opcode **`0x8D5A8B6B`**) carrying a **`remoteformat` XML**
string the client renders on-screen. It is carried as an IIR on the **world conversation**: opcode **`0x2000`**
(PassBlob), sender = `GameServerListener.WorldSenderEndpoint` (`0xB171CE13`/0x58, GameAgent), receiver = the player's GameInterface
(`Endpoints.GameInterfaceType 0x6C67C147` + `ClientInstance`).

**The top-of-screen FIFO banner** specifically uses the token family
**`showfifomessagefromserver(N)|context(M)|<Template>`** — AoC's "FIFO message" overlay (the queued
notifications at the top/centre of the screen). Templates seen in captures:
- `GenericDisplayAreaName` — the big **zone-name reveal** banner (text = a localized RDB string id).
- `GenericSimpleDynelPickup` — item/object **pickup** notice.
- `PvPDailyQuestAssassinationTracker` — PvP tracker line.

Plain tokens (no `showfifomessagefromserver` wrapper) render the smaller feedback lines: `AttributeChanged` /
`Increased` (stat up), `LostGraceEffect` / `AttackedByNPCLostGraceEffect` (buff worn off),
`CantCrouchInThisPlayfield`, `FeatUntrainNotEnoughMoney`, `NotCancelable`, ...

**The `AttributeChanged`/`Increased` line is the level-up feedback** (#209): there is **no** "You reached level N"
banner in any capture — when a character dings (or a quest awards attribute points) retail sends one
`AttributeChanged` message **per primary attribute that rose** (the 5 category-615 ids 804/806/808/810/814), then
a `0x96B8DC59` vitals push with the new maxima. Built by `AttributeChangedCodec` (`Faolan.Core.Network`, a string codec whose text
`FormatFeedbackCodec` carries), byte-exact in `AttributeChangedCodecTests`. typeFlags = `0x40000006`, footer = `0` (invariant across all 33 captured
messages). The three `key="..."` are per-burst nonces the client does not validate (reuse is in-client proven).
Wired into both ding paths: GM `.level` and the XP-driven `CombatService.PushLevelUp`. The growth amounts are
provisional (`Combat.AttributeGrowth`) pending real per-class tables.

This is the same `FormatFeedback` retail sends as the (textual) reply to `CancelBuff` — it is the general
"show feedback text" channel, distinct from the ChatServer chat/system-message path.

## Wire grammar (big-endian)

The packet body (after the 0x2000 opcode) is the IIR:
```
[u32 len]          = byte length of everything after this field
[u32 0x8D5A8B6B]   FormatFeedbackIIR sub-opcode
[u32 0x0000C350]   standard IIR marker
[u32 instance]     the target GameInterface object = the receiver endpoint instance = ClientInstance
                   (verified: in the Strie capture all 1249 GameInterfaceType packets AND the feedback share 0x02165EFF)
[u8  0x01]         lead flag (constant)
[u64 0]            reserved (constant 0)
[u32 typeFlags]    feedback type/priority (0x00000000 for the FIFO banner; 0x42000020 etc. for buff/attribute lines)
[u16 xmlLen][remoteformat XML]      the message (no null terminator)
[u32 footer]       trailing flag (0 or 1 in the captures)
```
Because the target instance == the world receiver instance (`ClientInstance`), a live send is **fully addressable
from the session** — not just a verbatim replay.

## What is built, and the two traps

- ✅ Decoded + byte-exact builder + golden test (`FormatFeedbackCodecTests`, round-trips the captured area-name
  banner and the LostGraceEffect line).
- ✅ Live send wired: `SendFeedback(client, xml, ...)` (world framing + opcode 0x2000). A standalone `.feedback`
  GM test command that replayed the proven `GenericDisplayAreaName` banner was **IN-CLIENT VERIFIED 2026-06-14**
  (on a live session, Darktemplarchar, it popped the "Castle approach" banner at the top of the screen, separate
  from chat); that throwaway test command has since been retired in the GM-command cleanup, but the verified send
  path (`SendFeedback`) remains and now fires from the level-up attribute feedback below.
- ✅ **`AttributeChanged` level-up feedback** (#209): byte-exact builder `AttributeChangedCodec` + golden test +
  decode script, wired into the GM `.level` and combat ding paths. Awaits in-client verification (`needs-testing`).
- ✅ **`LostGraceEffect` shield-loss line**: byte-exact `FeedbackXml.LostGraceEffect` (typeFlags
  `0x42000020`, footer `1`) + golden test, sent by `BreakShieldOnAggression` right before the Shield-of-the-Risen
  removal twin when the shield is lost to an aggressive action (see combat.md). The token+key is a fixed constant
  (byte-identical in two independent captures), not a per-send nonce.
- ⚠️ **No literal text crosses the wire.** Every FIFO template references a localized RDB string id (the
  area-name points at string 3474586) and the plain-token lines (`AttributeChanged`, ...) are token-driven, so
  server-authored custom text has no path here. Open work tracked in Gitea #664.
- ✅ **Resurrection-point discovered banner** (#372, verified in-client): byte-exact
  `RespawnPointDiscoveredCodec` + goldens, sent by `WorldSimulation.DiscoverNearbyRespawnPoints`
  alongside the spots re-send. Replaces a chat-routed `"Resurrection point discovered: X"` `SendSystemMessage` that
  was never capture-derived (and printed the RDB dev label, "ResurrectionAIPoint2"). Its `<parameter>` is a nested
  `<localized>` naming the point by its own id under category 270 — the client resolves the text, so no name crosses
  the wire. The nested per-point `key` is OPTIONAL (a keyless banner renders), which is the only reason this works
  for all 538 points: captures only ever discover 2, and the rest are underivable.
- ⚠️ **The area-name id is an LDB `50025` string id, and that id space is PER-BUILD.** The banner's nested element
  says so outright: `<localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|<id>'>`, so the
  client resolves the text out of its own LDB (`docs/rdb/ldb-string-index.md`) and no name crosses the wire. But the
  ids are **not stable content ids**: none of the **11** ids the corpus carries (48 sightings) exist in the current
  client's cat-50025 — while the *names* do, under different ids. `3474586` rendered **"Castle Approach"** in-client
  on 2026-06-14; the same text is now cat-50025 id **19303910**, and the RDB index was rewritten by a client patch on
  2026-07-23. Likewise "Bridge of Vilerus I" is `20356377`/`32857349` locally, not the corpus's `3373519`.
  So a harvested id can silently stop resolving after a client patch, and the durable form is to carry the area
  **name** and resolve it to the running client's id (crib-picking the English page the way `RdbLdbNameImporter`
  does). Open work in Gitea #377.
- ⚠️ **`id=` vs `token=` — do not enumerate feedback by `token=` alone.** A `remoteformat` identifies its string by
  EITHER; across the captures **175** frames use `token="…"` and **8** use `id="…"` (message ids 218327236, 64771509,
  23). #372 scanned for `token=` only, found nothing for discovery, concluded *retail sends no discovery toast*, and
  shipped a theory built on that absence — the message was there the whole time under `id=`. Match `<remoteformat `
  and read whichever attribute is present. A negative search result is not evidence until the search is verified.
