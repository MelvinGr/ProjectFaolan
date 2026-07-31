# Knubot conversation packets — wire grammar (Gitea #188)

Decoded from `AgeOfConan.exe` (Ghidra, 2026-06-14; reproduce with `tools/ghidra/DumpIirReaders.java`).
These are the NPC-dialogue packets served by `ConversationService.ServeScreen`. The **whole Knubot s2c set is
CONSTRUCTED** via `KnubotCodec` (`Faolan.Core.Network/Codecs/Dialogue/KnubotCodec.cs`) — `KnubotAppendText` (the
spoken line), `KnubotOpenChatWindow`, `KnubotAnswerList` (the option buttons) and `KnubotCloseChatWindow` —
entirely from the packet's typed columns, re-addressed to the live player + clicked NPC and framed on the
conversation endpoint (`GameAgent:<stored SenderInstance>`) like `SendConversationClose`. The quest-grant packets
in the flow are built by the `Codecs/Quest` codecs. `ServeScreen` **replays nothing** — there is no raw-`Data`
fallback (see below). Proven byte-identical to the codec construct (`ConversationConstructTests`/`KnubotCodecTests`).

**Stored structured (2026-06-30; screens columnized + `Data` retired 2026-07-12).** Every packet of a node is
decoded into structured `ConversationPacket` columns (`ConversationScreenDecoder` → `SubOpcode/Env/Field/Flag`,
plus `SenderInstance` and `CameraParams` for the camera) and BUILT wholly from those columns at send time — the
raw `Data` blob is **gone** (the importer REJECTS, rather than stores, a packet it can't decode to a recognised
sub-opcode, so there is nothing left to replay). The remoteformat screen(s) are stored as
**typed `ConversationScreen` rows** (`id`/`category`/`key`/`knubot`/`antitag` + ordered `ConversationScreenParameter`
child rows of `name`/`InnerRaw`) — the old JSON-array-of-XML `ScreensJson` blob is **gone**. The importer
columnizes each screen via `ConversationScreenCodec.Parse`, which throws unless the typed round-trip
reproduces the captured XML byte-for-byte (652/652 screens, 6090 parameters); the runtime rebuilds the wire XML
from the rows via `ConversationScreenColumns.ToScreen` → `RemoteFormatScreenCodec.Build`, with **no fallback** — the
columns are authoritative. The screen TEXT (`key`) is the captured template; the content `key` is non-computable
(same as the item TEA keys).

### Keys — derived, so a re-import is byte-stable

The tree carries **no surrogate ids**. A `ConversationNode`'s primary key is DERIVED from its identity —
`ConversationNode.ComputeId(NpcName, TypeId, AnswerPath, ConditionQuestId, ConditionQuestState)`, FNV-1a over a
NUL-separated rendering — and each child is keyed by its parent's key plus its own `Sequence`:
`ConversationPacket` = `(node, Sequence)`, `ConversationScreen` = `(node, PacketSequence, Sequence)`,
`ConversationScreenParameter` = `(node, PacketSequence, ScreenSequence, Sequence)`.

The importer rebuilds the whole tree on every re-harvest (`conversations` is an `Overwrite`-only module: the
merge scores each screen across ALL captures and keeps the winner, so it is not incremental). With
AUTOINCREMENT that renumbered all 8781 rows every run, churning `faolan.sql` for no semantic change; with
derived keys two runs over the same corpus produce byte-identical tables. Two consequences worth knowing:

- The derived id also **enforces** the identity, which the old unique index could not — two of its columns are
  nullable and SQL treats NULLs as distinct, so the 232 unconditional nodes were never constrained by it.
- Because a re-import reuses the same keys, the rebuild must DELETE before it stages the inserts
  (`ExecuteDeleteAsync`); queuing both into one `SaveChanges` races them on the same primary key.

`NpcName` is non-nullable and is the identity the runtime routes on; a node whose NPC could not be named from
its capture stores the `#<handle>` fallback the merge already dedups on.

### Session slots — regenerated, never stored (#417)

A screen carries no dialogue text: `id`+`category` name a string in the CLIENT's LDB and each `<parameter>` fills
one of that template's placeholders. Three of them are **session state** rather than content, and all three are
regenerated per send by `RemoteFormatSession.WithLivePlayer` from the live character, their guild and the realm:

| parameter | captured value | what we send |
|---|---|---|
| `name` | the capturing player's character | the live character's name (captured `flags` kept — the gender the localized text declines against) |
| `guildname` | their guild, or empty | the live character's guild (empty when guildless), looked up by `Character.GuildId` |
| `npcguildname` | `Not in guild [<retail server>]` | `Not in guild [<our realm's name>]` (`RemoteFormatSession.NotInGuildLabel`) |

The importer therefore stores them **blank** (`ConversationScreenColumns.ToStoredRow`, applied after `ConversationScreenCodec.Parse`'s
byte-exact gate): the slot stays — the client parses the screen by parameter name and order, and `flags` matters —
but the string goes, so no captured character / guild / retail-server name reaches `faolan.db` or the committed
`faolan.sql` seed. `SeedSessionDataTests` gates that on the shipped seed. `npcguildname` also has a second captured
shape, the `<localized token="StrangerGuild" category="100">` reference; that one is content and is kept verbatim.
Only the guildless label is attested (no captured NPC belongs to a guild), so it is the only one we build.

`AnswerList` (0xF164A4A5) and `CloseChatWindow` (0xC4AF39EB) **share one grammar** (validated 163/163 +
40/40 across the structured rows): the KnubotBase header then `[u32 count][ count x ([u16 len][remoteformat string]) ]`. CloseChatWindow
is that shape with a single empty `id="0"` remoteformat (retail's "nothing (more) to say").

**Carrier framing (confirmed against the captures):** every Knubot s2c packet is opcode `0x2000`, sender
`GameAgent` (`0xB171CE13`) : `<conversationAgentInstance>`, receiver `GameInterface` (`0x6C67C147`) :
`<playerHandle>`. Retail **varies the GameAgent sender instance per conversation** — `0x07, 0x08, 0x0a, 0x0b,
0x0e, 0x10, 0x11, 0x54..0x5a` observed (`0x08`/`0x0e` most common) — and the client addresses its `KnubotAnswer`
(c2s: `GameInterface:<player>` → `GameAgent:<thatInstance>`) back to whichever instance sent the screen. So the
construct path **preserves the captured sender instance** (the `ConversationPacket.SenderInstance` column, decoded
by `ConversationScreenDecoder`) rather than fixing one, matching retail byte-for-byte.

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
stores the GameCharAgent sender instance + the param bytes in the `SenderInstance`/`CameraParams`
columns, and the runtime BUILDS the packet from those columns via
`SetDialogueCameraCodec` (framed by `ConversationService.BuildDialogueCameraPacket`) re-addressed to the live player + NPC handles (byte-exact in
`ConversationConstructTests`). The interleaved quest-grant packets are built by the `Codecs/Quest` codecs.

Open work is tracked in Gitea #188.
