# Knubot conversation packets — wire grammar (Gitea #188)

Decoded from `AgeOfConan.exe` (Ghidra, 2026-06-14; reproduce with `Captures/ghidra/DumpIirReaders.java`).
These are the NPC-dialogue packets served by `ConversationService.ServeScreen`. The **whole Knubot s2c set is
now CONSTRUCTED** via `KnubotCodec` (`Faolan.Core.Network/Codecs/Dialogue/KnubotCodec.cs`) — `KnubotAppendText` (the
spoken line), `KnubotOpenChatWindow`, `KnubotAnswerList` (the option buttons) and `KnubotCloseChatWindow`:
`TryConstructKnubotRecord` lifts the remoteformat XML / options out of the captured record and rebuilds them
re-addressed to the live player + clicked NPC, framed on the conversation endpoint (`GameAgent:<captured
instance>`) like `SendConversationClose`. The quest-grant packets in the flow are built by the `WorldQuest`
builders. So `ServeScreen` no longer replays any packet — substitution-replay survives only as a fallback for
an unexpected sub-opcode. Proven byte-identical to the old handle-substitution
(`ConversationConstructTests`/`KnubotCodecTests`).

**Stored structured (2026-06-30; screens fully columnized 2026-07-12).** Every packet of a node is decoded into
structured `ConversationPacket` columns (`ConversationScreenDecoder` → `SubOpcode/Env/Field/Flag`, plus
`CameraAgentInstance/CameraParams` for the camera) and BUILT from those columns at send time, not from the raw
`Data` blob (kept only as a fallback for non-Knubot packets). The remoteformat screen(s) are now stored as
**typed `ConversationScreen` rows** (`id`/`category`/`key`/`knubot`/`antitag` + ordered `ConversationScreenParameter`
child rows of `name`/`InnerRaw`) — the old JSON-array-of-XML `ScreensJson` blob is **gone**. The importer
columnizes each screen via `ConversationScreenColumns.ToRow`, which throws unless the typed round-trip
reproduces the captured XML byte-for-byte (652/652 screens, 6090 parameters); the runtime rebuilds the wire XML
from the rows via `ConversationScreenColumns.ToScreen` → `RemoteFormatXml.Build`, with **no fallback** — the
columns are authoritative. The **live player name/guild are regenerated** into each screen's `name`/`guildname`
parameters (`RemoteFormatXml.WithLivePlayer`) so the NPC greets the real character — the captured player's name
is gone. **Remaining:** the screen TEXT (`key`) is still the captured template; generating it from RDB text +
the Lua/tag VM is blocked on the harvested, client-validated content `key` (non-computable, same as the item
TEA keys).

`AnswerList` (0xF164A4A5) and `CloseChatWindow` (0xC4AF39EB) **share one grammar** (validated 163/163 +
40/40 across the structured rows): the KnubotBase header then `[u32 count][ count x ([u16 len][remoteformat string]) ]`. CloseChatWindow
is that shape with a single empty `id="0"` remoteformat (retail's "nothing (more) to say").

**Carrier framing (confirmed against the captures):** every Knubot s2c packet is opcode `0x2000`, sender
`GameAgent` (`0xB171CE13`) : `<conversationAgentInstance>`, receiver `GameInterface` (`0x6C67C147`) :
`<playerHandle>`. Retail **varies the GameAgent sender instance per conversation** — `0x07, 0x08, 0x0a, 0x0b,
0x0e, 0x10, 0x11, 0x54..0x5a` observed (`0x08`/`0x0e` most common) — and the client addresses its `KnubotAnswer`
(c2s: `GameInterface:<player>` → `GameAgent:<thatInstance>`) back to whichever instance sent the screen. So the
construct path **preserves the captured sender instance** (`ConversationService.ReadSenderInstance`) rather than
fixing one, matching retail/replay byte-for-byte.

Read primitives (shared with the rest of the protocol): `FUN_0048f900` = read u32 (byte-swapped when the
big-endian flag is set), `FUN_0048f840` / `FUN_006200a0` = read a length-prefixed string (the
`<remoteformat>` text token), `FUN_00cba1b0(&x, n)` = read n raw bytes.

## Common base — `KnubotBaseIIR_c::ReadSubClass` (`FUN_009a7e00`)
```
u16  version (== 1)
u32  fieldA          (-> obj+0x1c)   — NPC/handle or conversation id
u32  fieldB          (-> obj+0x20)
```
Every Knubot packet begins with this base (the subclasses call it first).

## The screen packets (over the base)
| packet | sub-op | reader | body after base |
|---|---|---|---|
| **OpenChatWindow** | `0x045B0B0F` | `FUN_009a5490` | window framing (small) |
| **AppendText** | `0x4A3D899A` | `FUN_009a4ce0` | one `<remoteformat>` string = the NPC's line |
| **AnswerList** | `0xF164A4A5` | `FUN_009a3a10` | a sub-object, then `u32 answerCount` (asserted `>0`, `<=1000`), then `answerCount × <remoteformat>` strings = the player's options |
| **CloseChatWindow** | `0xC4AF39EB` | — | AnswerList with `answerCount = 0` (the captured constant in `ConversationService`) |

A dialogue SCREEN on the wire = `OpenChatWindow` (opens) → `AppendText` (NPC text) → `AnswerList` (the
option list). Each text/option slot is a `<remoteformat id=.. category=.. key=..>` token the client resolves
against its RDB — same deterministic-`key` mechanism as the quest journal
; so building a screen = emit base + the OpenChatWindow/AppendText/AnswerList
records with the right `(id, category, key)` refs.

## The non-dialogue packets in a node (`SetDialogueCamera`)

A captured conversation node also carries packets that are **not** Knubot dialogue text (~72 of 511 rows in
`faolan.db`). These are **`DispatchSetDialogueCamera`** — opcode **`0x2015`**, caller **GameCharAgent
(`0xA9ECB95D`)** → receiver **GameCharInterface (`0x8B87F791`)** (a different endpoint pair than the Knubot
dialogue's GameAgent→GameInterface `0x2000`). The body is the cinematic conversation camera's two focus
entities + params:

```
[u32 0xC350][u32 npcHandle][u32 0xC350][u32 playerHandle][u32 0x05010119][trailer]
```

They carry no `remoteformat` text, but they ARE structured (#188): `ConversationScreenDecoder.Decode`
stores the GameCharAgent sender instance + the param bytes in the `CameraAgentInstance`/`CameraParams`
columns, and the runtime BUILDS the packet from those columns via
`DialogueCameraBody.BuildDialogueCameraPacket` re-addressed to the live player + NPC handles (byte-exact in
`ConversationConstructTests`). The interleaved quest-grant packets are built by the `WorldQuest` builders.

## Remaining for #188
- Harvest the dialogue TREE (screens + option remoteformat refs + branching) from the RDB (the captures
  already give the screens; `memory rdb_extraction` for the RDB side).
- Build `KnubotOpenChatWindow`/`AppendText`/`AnswerList` from those refs (byte-exact golden tests vs the
  captured `ConversationPacket` bodies), then drive them from the Lua/tag VM (conditions + grants already
  landed, #37) instead of replaying. Retire `ConversationService.ServeScreen`'s verbatim path.
