# Knubot-conversation + misc IIR readers

Wire grammars for the Knubot conversation records plus a batch of misc combat/world IIR records,
recovered from `AgeOfConan.exe` via vtable-xref (`tools/ghidra/DumpVtableReaders.java`, slot 8)
and the shared helpers. Validator: `ConversationConstructTests` — every reader below
consumes **exactly to the declared packet length** for every captured instance.

Stream primitives + the dynel-style framing `[len][sub][0xC350][handle][u8 env][reader]` are as in
[iir-readers-combat-world.md](iir-readers-combat-world.md).

## Misc readers (dynel-style env byte)

| sub | type | reader | grammar (after `env`) |
|---|---|---|---|
| `0x1F4CB0F7` | `PetIIR_c` | `7a8bc0` | `[u32 mode]`, then `mode∈{0,1}`→`[u32×2]`, then `mode==0`→`[u32×3]` |
| `0xBA1FD222` | `FeedbackIIR_t` | `79f8e0` | `[u32×3][u8]` (the `u8 > 2` short-circuits, but no extra fields) |
| `0xC7954504` | `CrimeIIR_c` | `148c9f0` | `[u8 flag]`, then `flag==1`→`[u32]` |
| `0xB066AFC2` | `SimpleCharKilledByUpdateIIR_t` | `148b8f0` | `[u8 version]`, then `version==1`→`[u32×2]` (killer + flags) |
| `0x10D27BC0` | `IntendedTeleportIIR_c` | `5bfce0` | PlayfieldProxy stream + `[u8 bool]` |
| `0xCE30BE4E` | `AreaFormulaIIR_t` | `79e330` | 0x3F1-list of pairs + `[u32]` + 0x3F1-list of triples + `[u32]` |

### PlayfieldProxy stream (`FUN_00a6a600`, used by IntendedTeleport)

```
[u8 version == 'b' (0x62)]    # else "Invalid version of the PlayfieldProxy stream, ... protocol error"
[u32 ×8]                      # the playfield/proxy descriptor
[u8 bool]
```

### 0x3F1 list framing (`FUN_0079eb70` pairs / `FUN_0079ec30` triples)

A count word obeys the catalog-wide invariant `count = (n + 1) * 0x3F1` (1009); the body is then
`n` records of 2 u32s (`eb70`) or 3 u32s (`ec30`). AreaFormula chains a pair-list then a triple-list,
each followed by a trailing scalar.

## Knubot conversation readers

The Knubot conversation carrier nests **both** endpoints: the player is the carrier handle, and the
`KnubotBaseIIR_c` header (`FUN_009a7e00`) carries the version + the NPC endpoint:

```
[u32 len][u32 sub][u32 0xC350][u32 playerHandle]
[u8 env][u16 version == 1][u32 0xC350][u32 npcHandle]   # KnubotBaseIIR_c header, ends @ offset 27
[ type-specific body ]
```

| sub | type | reader | body after header |
|---|---|---|---|
| `0x4D7D9123` | `KnubotAnswerIIR_c` | `9a5d90` | `[u32]` (selected answer id) |
| `0x045B0B0F` | `KnubotOpenChatWindowIIR_c` | `9a5590` | `[u8]` |
| `0x7446F3FA` | `KnubotFinishTradeIIR_c` | `9a7c20` | `[u32 bool][u64]` |
| `0x4A3D899A` | `KnubotAppendTextIIR_c` | `9a4de0` | `[u32 TextType][u16 strLen][str][u8 bool]` |
| `0xC4AF39EB` | `KnubotCloseChatWindowIIR_c` | `9a58d0` | `[u32 TextType][u16 strLen][str]` |
| `0xF164A4A5` | `KnubotAnswerListIIR_c` | `9a3a10` | `[u32 nAnswerCount][ count × ([u16 len][str]) ]` |

**`TextType`** is the client's own name for the leading word: `KnubotAppendTextIIR_c::WriteSubClass`
(`FUN_009A4EC0`) logs *"NPC %d speaking with Character %d: TextType %d, Text length = %d"*. It is 1 on 897 of the
907 captured dialogue lines and 6 on the other 10; what those select is undecoded.

**`KnubotCloseChatWindow` is not the AnswerList body.** The two are indistinguishable on all 179
captured s2c closes, every one of which carries `[00000001][005F][<a 95-byte empty remoteformat>]` — identical
bytes whether the leading 1 is a list count with one string after it, or a `TextType` with one string after it.
The readers settle it: `9a3a10` validates `nAnswerCount` into 1..1000 **by name** and then loops, while `9a58d0`
reads one `u32` into `this+0x34` and one `std::string` into `this+0x38` with no loop — and its `WriteSubClass`
logs that string as *"Text length"*. Reading the close as a list refuses the 7 captured **c2s** records, whose
`[00000000][0000]` is a `TextType` of 0 and an empty text.

**`KnubotAppendTextIIR_c`** is the NPC dialogue line: its length-prefixed string is the
`remoteformat` **XML dialogue payload**, e.g.

```xml
<remoteformat id="7731" category="50002" key="..." knubot="388" antitag="2">
  <parameter value="7;&lt;localized token=&quot;Stranger&quot; ... name=" a" />
  <parameter value="7;&lt;stringentry string=&quot;Hoxxling&quot; ... name="name" />
  ...
</remoteformat>
```

i.e. the same dialogue content the higher-level [ConversationService](../../Faolan/GameServer/Dialogue/)
+ [knubot-conversation-packets.md](../subsystems/knubot-conversation-packets.md) drive — this pins the exact reader
framing (header + u16-length string).

## No-op / out-of-band readers

- `MoveInventoryItemIIR_t` (`0x2A8A1B49`, `FUN_005b9230`) — `ReadSubClass` returns 0; the move is
  carried out-of-band by the `ProtoMoveInventoryItemIIR` message, not this IIR reader.

## Sub-reader bodies (structure noted)

- `SpellBookIIR_t` (`0x6BA10936`, `FUN_00795ba0`) — delegates the whole body to `FUN_0077af90`
  (the spell-list reader).
- `TemplateActionIIR_t` (`0x6C300937`, `FUN_007a0b00`) — `FUN_005d5150` sub-record then `[u32×6]`.
- `KnubotStartTradeIIR_c` (`0x4B2A47BF`, `FUN_009a63a0`) — Knubot header + `[u64]` + a count-prefixed
  list of `TradeItem_c` (string + 4 u32 each).

Their codecs carry the variable sub-bodies verbatim, byte-exact; see [v1-iir-readers.md](v1-iir-readers.md).

## Decode coverage

Decoded + dissected (this doc + `ConversationConstructTests`). The Knubot readers feed the
dialogue VM; the combat/teleport ones feed combat and the simulation layer.
