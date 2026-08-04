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

### The dialogue is a GRAPH, keyed by what each screen says (Gitea #642)

Three tables, and between them they are retail's own shape:

| table | is | keyed by |
|---|---|---|
| `ConversationNode` | one screen | `(NpcName, ContentKey, Discriminator)` |
| `ConversationEdge` | one branch — clicking option *X* on screen *A* serves screen *B* | `(FromNodeId, OptionId, ConditionQuestId, ConditionQuestState)` |
| `ConversationEntry` | one root — what OPENS the conversation | `(NpcName, Trigger, TriggerWorldObjectId, ConditionQuestId, ConditionQuestState)` |

`ContentKey` is the ordered `category:id` list of the screen's remoteformat screens — the NPC's spoken line
(category **50002**), each option button (**50001**; `0` is the universal *"Goodbye"*, shared by 417 screens) and
the close screen (**100:0**). In the shipped data that `(id, category)` pair determines the LDB decryption key
1:1, so it is a content-derived name for the screen. A **spurious trailing close** is excluded, exactly as
`ConversationService.ServeScreen` excludes it: a captured screen that offers options sometimes also carries a
`CloseChatWindow` because the capturing session shut the window right after, and the runtime drops that packet so
the option list stays interactive.

`Trigger` is `Talk` / `UseWorldObject` / `Approach` — the three ways retail opens a window, replacing the
`ServerInitiated` + `TypeId 0` + `AnswerPath == ""` trio that used to encode it across three columns.

⚠️ **It used to be keyed by the answer-index PATH the player walked** (`""`, `"0"`, `"0>1"`, …), chosen before we
could read the dialogue's own identifiers off the wire. That key was provably lossy against the shipped data:

- **It could not represent a loop.** Three genuine cycles exist — Bhaltair of Lacheish (7 screens), Guard Captain
  Marcus (4) and Hadrathus (2) — and a path has to unroll them, which is why `AnswerPath` reached depth 15. It
  could only unroll as far as some capture happened to walk, so **going round one of these menus twice closed the
  window on the player**. As an edge back into the graph it just works, with no new capture.
- **It stored every convergent screen once per route.** 553 harvested walks are 511 distinct screens.
- **An answer INDEX means different things in different variants of one screen.** Casilda's greeting has six
  option lists across its quest-state variants; index `0` is `17474` / `8176` / `7615` / `8149` / `12557` / `7617`
  depending on which was served. The option's own LDB string id means one thing everywhere, so the index is
  resolved against the screen actually on the player's display and never persisted.

The tree beneath a screen carries **no surrogate ids** either: each child is keyed by its parent's key plus its own
`Sequence` — `ConversationPacket` = `(node, Sequence)`, `ConversationScreen` = `(node, PacketSequence, Sequence)`,
`ConversationScreenParameter` = `(node, PacketSequence, ScreenSequence, Sequence)`. The three roots are
`DerivedKey` ids (FNV-1a over a NUL-separated rendering of the identity above). With AUTOINCREMENT a re-harvest
renumbered ~17k rows every run, churning `faolan.sql` for no semantic change; derived keys make two runs over the
same corpus produce byte-identical tables. Two consequences worth knowing:

- Because a re-import reuses the same keys, the rebuild must DELETE before it stages the inserts
  (`ExecuteDeleteAsync`), and it must drop the WIRING before the screens — an edge names two nodes and its inbound
  end deliberately does not cascade, so deleting a screen underneath a live edge is a foreign-key violation.
- `conversations` is **`Overwrite`-only**. The fold is a global partition over every harvested walk — which screens
  merge, and which need a `Discriminator`, is decided by comparing them against each other — so it cannot be
  applied to one new dump against a database that has forgotten the walks the rest came from. The merge rule the
  old incremental upsert existed for (*the most complete capture of a screen wins*) runs inside the fold instead,
  across all captures at once.

`NpcName` is non-nullable and is the identity the runtime routes on; a node whose NPC could not be named from
its capture stores the `#<handle>` fallback the merge already dedups on.

### The fold: from harvested walks to the graph

A capture is a **walk** — the player opened a window and clicked down one route — so `ConversationExtractor`
produces one row per (route position, quest state). `ConversationGraph.Fold` turns those into the graph:

1. **Signature** each walk by its `ContentKey`.
2. **Partition-refine** within each signature group: two same-content walks MERGE unless a *shared* `(option, gate)`
   slot leads to children in different classes. Shared-only is what lets a truncated loop lap merge into the fuller
   screen instead of splitting off; iterate to a fixed point (terminates, and handles cycles).
3. **Emit** one node per class, `Discriminator` assigned deterministically within the group.
4. **Emit** the edges and roots from the walks' parent links.

The parent link is **harvested, not derived**: `ConversationWalk.ParentContentKey` / `OptionId` name the screen the
player was looking at when they picked and the option they clicked, both read off the wire. A path only says "one
pick shallower", and where a screen has quest-gated variants that is several different screens — Casilda's `2`
sub-tree was harvested under her five-option `12373` greeting but is itself ungated, so a path-derived parent
attached it to the three-option unconditional greeting and index 2 named *"Goodbye"*. Where the harvested link does
not resolve (7 branches: the capture that offered the option lost the merge) the path is the fallback, and the
count is reported rather than assumed.

**The acceptance gate is a walk replay.** Every harvested walk must still be reachable by starting at the root its
own capture opened on and clicking the same sequence of option ids: **549 of 553** on the shipped corpus, with the
4 that are not named in the import log. The fold also reports every screen nothing reaches — a class of defect the
answer-path key could not even express, because there the path *was* the route.

Two irreducible facts the corpus states, both reported by the import:

- **`Discriminator` is not decoration.** Laranga's `19827` (options: just *"Goodbye"*) is two different screens:
  one route's *"Goodbye"* goes to `20148`, the other's to `1384`.
- **The grant rides the branch**, not the screen. Turach's `2832` was harvested twice with identical content, once
  granting quest 12371 and once granting nothing.

### Quest-state variants — derived from the captures, not from capture names (Gitea #639)

One NPC says different things at different points in a chain, and every version says it at the same point in the
dialogue. The gate that tells them apart — `ConditionQuestId` + `ConditionQuestState` — is
**derived** by `ConversationVariants.SplitConflicts` from the quest state each capture was in when the screen was
served (`ConversationEntry.QuestStates`: every quest that capture has seen a full-update or a state flip for, in
its latest state).

The split is **conflict-driven**: a group is only gated when its captures actually disagree about what the screen
says (the `remoteformat` `category:id` list — `ConversationVariants.Signature`). Greeting a vendor while an
unrelated quest happens to be active must not make that vendor conditional on it, and nearly every NPC stays
unconditional. Within a group that does disagree:

- The classes are ordered by how much of the player's quest state their captures knew. The **least-informed**
  class is the unconditional fallback — what a character with no chain progress gets.
- Every other class is gated on a `(quest, state)` pair *all* of its captures agree on and that no
  less-or-equally-informed class carries. **Active** is preferred over Completed where both would do.
- Two classes the captured state cannot tell apart share one gate, and `ConversationExtractor.Merge`'s
  packet-count tiebreak keeps the richer. That is scoped to the pair — it does not cost the group its other gates.

Gates therefore **overlap** by construction: a player deep in a chain still holds every earlier quest of it, so
Casilda's escort greeting (`12407` Active) and the two greetings after it all pass at once. The runtime resolves
that by serving the variant gated on the quest the player accepted **most recently**
(`ConversationService.PickByLatestQuest` — `CharacterQuest` is insert-ordered, so the highest row id is the beat
the player is on), falling back to the unconditional variant when no gate passes.

⚠️ This replaced a five-row table (`ConversationConditions`) keyed on capture **file names**. It made itself
authoritative for every NPC it named, so a Casilda screen from any capture nobody had listed was silently
dropped: adding a capture got nothing, renaming one lost the gate. Deriving it instead recovers her in-progress
greeting and adds four further chain-stage variants (`12369` / `12373` / `12407`), and turns the old
`13480 Completed` post-freeing gate into `12407 Active` — the same beat, stated by the data.

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
