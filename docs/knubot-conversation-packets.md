# Knubot conversation packets — wire grammar (Gitea #188)

Decoded from `AgeOfConan.exe` (Ghidra, 2026-06-14; reproduce with `Captures/ghidra/DumpIirReaders.java`).
These are the NPC-dialogue packets `ConversationService.ServeScreen` currently **replays verbatim** from the
captured `ConversationPacket` rows (~409 rows). Decoding them is the step toward constructing dialogue from
RDB text + the Lua/tag VM (#188), retiring the replay.

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
(`docs/quest-log-handover.md`); so building a screen = emit base + the OpenChatWindow/AppendText/AnswerList
records with the right `(id, category, key)` refs.

## Remaining for #188
- Harvest the dialogue TREE (screens + option remoteformat refs + branching) from the RDB (the captures
  already give the screens; `memory rdb_extraction` for the RDB side).
- Build `KnubotOpenChatWindow`/`AppendText`/`AnswerList` from those refs (byte-exact golden tests vs the
  captured `ConversationPacket` bodies), then drive them from the Lua/tag VM (conditions + grants already
  landed, #37) instead of replaying. Retire `ConversationService.ServeScreen`'s verbatim path.
