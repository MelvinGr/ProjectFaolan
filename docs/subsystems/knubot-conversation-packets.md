# Knubot conversation packets — wire grammar

Decoded from `AgeOfConan.exe` (reproduce with `tools/ghidra/DumpIirReaders.java`). These are the NPC-dialogue
packets served by `ConversationService.ServeScreen`. The whole Knubot s2c set is constructed via `KnubotCodec`
(`Faolan.Core.Network/Codecs/Dialogue/KnubotCodec.cs`) — `KnubotAppendText` (the spoken line),
`KnubotOpenChatWindow`, `KnubotAnswerList` (the option buttons) and `KnubotCloseChatWindow` — entirely from the
packet's typed columns, re-addressed to the live player + clicked NPC and framed on the conversation endpoint
(`GameAgent:<stored SenderInstance>`) like `SendConversationClose`. The quest-grant packets in the flow are built by
the `Codecs/Quest` codecs. `ServeScreen` replays nothing; it is byte-identical to the codec construct
(`ConversationConstructTests`/`KnubotCodecTests`).

**Storage.** Every packet of a node is decoded into structured `ConversationPacket` columns
(`ConversationScreenDecoder` → `SubOpcode/Env/Field/Flag`, plus `SenderInstance` and `CameraParams` for the camera)
and built wholly from those columns at send time; the importer rejects, rather than stores, a packet it cannot decode
to a recognised sub-opcode. The remoteformat screen(s) are typed `ConversationScreen` rows
(`id`/`category`/`key`/`knubot`/`antitag` + ordered `ConversationScreenParameter` child rows of `name`/`InnerRaw`).
The importer columnizes each screen via `ConversationScreenCodec.Parse`, which throws unless the typed round-trip
reproduces the captured XML byte-for-byte (652/652 screens, 6090 parameters); the runtime rebuilds the wire XML from
the rows via `ConversationScreenColumns.ToScreen` → `RemoteFormatScreenCodec.Build`, with no fallback. The screen
text `key` is the captured template; the content `key` is non-computable (same as the item TEA keys).

### The dialogue is a graph, keyed by what each screen says

Three tables, and between them they are retail's own shape:

| table | is | keyed by |
|---|---|---|
| `ConversationNode` | one screen | `(NpcTemplateId, ContentKey, Discriminator)` |
| `ConversationEdge` | one branch — clicking option *X* on screen *A* serves screen *B* | `(FromNodeId, OptionId, ConditionQuestId, ConditionQuestState)` |
| `ConversationEntry` | one root — what opens the conversation | `(NpcTemplateId, Trigger, TriggerWorldObjectId, ConditionQuestId, ConditionQuestState)` |

`ContentKey` is the ordered `category:id` list of the screen's remoteformat screens — the NPC's spoken line
(category 50002), each option button (50001; `0` is the universal *"Goodbye"*, shared by 417 screens) and
the close screen (100:0). In the shipped data that `(id, category)` pair determines the LDB decryption key
1:1, so it is a content-derived name for the screen. A spurious trailing close is excluded, exactly as
`ConversationService.ServeScreen` excludes it: a captured screen that offers options sometimes also carries a
`CloseChatWindow` because the capturing session shut the window right after, and the runtime drops that packet so
the option list stays interactive.

`Trigger` is `Talk` / `UseWorldObject` / `Approach` — the three ways retail opens a window.

The graph is keyed by content, not by the answer-index path the player walked (`""`, `"0"`, `"0>1"`, …), because a
path key is lossy against the shipped data:

- **A path cannot represent a loop.** Three genuine cycles exist — Bhaltair of Lacheish (7 screens), Guard Captain
  Marcus (4) and Hadrathus (2). A path has to unroll them (to depth 15), only as far as some capture happened to
  walk, so going round one of these menus twice closes the window. As an edge back into the graph it just works,
  with no new capture.
- **A path stores every convergent screen once per route.** 553 harvested walks are 511 distinct screens.
- **An answer index means different things in different variants of one screen.** Casilda's greeting has six
  option lists across its quest-state variants; index `0` is `17474` / `8176` / `7615` / `8149` / `12557` / `7617`
  depending on which was served. The option's own LDB string id means one thing everywhere, so the index is
  resolved against the screen actually on the player's display and never persisted.

The tree beneath a screen carries no surrogate ids either: each child is keyed by its parent's key plus its own
`Sequence` — `ConversationPacket` = `(node, Sequence)`, `ConversationScreen` = `(node, PacketSequence, Sequence)`,
`ConversationScreenParameter` = `(node, PacketSequence, ScreenSequence, Sequence)`. The three roots are
`DerivedKey` ids (FNV-1a over a NUL-separated rendering of the identity above), so two runs over the same corpus
produce byte-identical tables (an AUTOINCREMENT key would renumber ~17k rows per re-harvest). Two consequences:

- Because a re-import reuses the same keys, the rebuild must DELETE before it stages the inserts
  (`ExecuteDeleteAsync`), and it must drop the WIRING before the screens — an edge names two nodes and its inbound
  end deliberately does not cascade, so deleting a screen underneath a live edge is a foreign-key violation.
- `conversations` is `Overwrite`-only. The fold is a global partition over every harvested walk — which screens
  merge, and which need a `Discriminator`, is decided by comparing them against each other — so it cannot be
  applied to one new dump against a database that has forgotten the walks the rest came from. The merge rule
  (*the most complete capture of a screen wins*) runs inside the fold, across all captures at once.

#### Who says it is the NPC's template, not its name

`NpcTemplateId` is non-null and is what the runtime routes on, end to end: the click resolves the handle to a
template (`INetworkClientSession.SpawnedNpcTemplatesByHandle`), `GetConversationEntries` looks the roots up by it,
the quest-giver map and the approach-conversation set are keyed by it, and `TravelDestination` is reached through
the dialogue branch that is its primary key. `NpcName` is a label — logs, reports, the travel refusal text —
and is empty where a capture never named the NPC.

A display name is ambiguous in the world — `Halen van Dummy` is the internal name of 38 templates,
`Conarch Village Guard` and `Smuggler` of 5 each — while a template is exactly one kind of NPC; `Shop` keys the same
way. A harvested walk whose NPC resolves to no template (or to one the pruned world did not import) is dropped with a
report line rather than stored as an unreachable row — the runtime could never route a click to it.

Quest goals follow the same rule. `QuestGoalDefinition.TargetTemplateId` resolves
the goal's target to a template at import, so both the TalkToNpc goal match and `GetQuestGatedNpcMap` compare
template against template; the name survives as the fallback for the 586 goals naming NPCs on maps not yet
imported (open work tracked in Gitea #521); a name-keyed gate hides the wrong same-named NPC — see
[`quests.md`](quests.md). The Escort goal never needed it: its `TrailingRefId` really is the escortee's template
(Casilda 9411 on quest 12407). A TalkToNpc goal's `TrailingRefId` is the item the player hands over, not the NPC —
quest 12371's Turach goal carries `193400`, the Shipwrecked stone brick — so it has no id of its own to match on
and needs the resolved column.

### The fold: from harvested walks to the graph

A capture is a walk — the player opened a window and clicked down one route — so `ConversationExtractor`
produces one row per (route position, quest state). `ConversationGraph.Fold` turns those into the graph:

1. **Signature** each walk by its `ContentKey`.
2. **Partition-refine** within each signature group: two same-content walks merge unless a *shared* `(option, gate)`
   slot leads to children in different classes. Shared-only is what lets a truncated loop lap merge into the fuller
   screen instead of splitting off; iterate to a fixed point (terminates, and handles cycles).
3. **Emit** one node per class, `Discriminator` assigned deterministically within the group.
4. **Emit** the edges and roots from the walks' parent links.

The parent link is harvested, not derived: `ConversationWalk.ParentContentKey` / `OptionId` name the screen the
player was looking at when they picked and the option they clicked, both read off the wire. A path only says "one
pick shallower", and where a screen has quest-gated variants that is several different screens — Casilda's `2`
sub-tree was harvested under her five-option `12373` greeting but is itself ungated, so a path-derived parent
attaches it to the three-option unconditional greeting, where index 2 names *"Goodbye"*. Where the harvested link does
not resolve (8 branches: the capture that offered the option lost the merge) the path is the fallback, and the
count is reported rather than assumed. That fallback shortlists on what each candidate actually offered — a
path names one walk per quest-gated variant of the screen, and a variant whose answer list the capture never
recorded has no button for the clicked index, so it cannot be the parent whatever its gate says.

**The acceptance gate is a walk replay.** Every harvested walk must still be reachable by starting at the root its
own capture opened on and clicking the same sequence of option ids: 553 of 553 on the shipped corpus, with any
that are not named in the import log. The fold also reports every screen nothing reaches — a defect an answer-path
key cannot even express, because there the path is the route.

**A hop is replayed under the gate that hop was harvested under, not the walk's own.** The route is
a chain of harvested (parent, child) pairs and the gate is evaluated per pair, so a route can cross an ungated
screen on its way to a gated one and back. Turach's `10320`-Active greeting offers option `2142` both
unconditionally and gated on `12407` Completed, leading to two different screens; matching the *leaf* walk's gate
at that first hop finds neither, falls through to the unconditional branch, and the rest of the route does not exist
there. Laranga is the mirror image — a `10320`-Completed leaf hanging off three ungated hops, whose gate matches a
*sibling* branch at hop 1.

Two irreducible facts the corpus states, both reported by the import:

- **`Discriminator` is not decoration.** Laranga's `19827` (options: just *"Goodbye"*) is two different screens:
  one route's *"Goodbye"* goes to `20148`, the other's to `1384`.
- **The grant rides the branch**, not the screen. Turach's `2832` was harvested twice with identical content, once
  granting quest 12371 and once granting nothing.

### Quest-state variants — derived from the captures, not from capture names

One NPC says different things at different points in a chain, and every version says it at the same point in the
dialogue. The gate that tells them apart — `ConditionQuestId` + `ConditionQuestState` — is
derived by `ConversationVariants.SplitConflicts` from the quest state each capture was in when the screen was
served (`ConversationEntry.QuestStates`: every quest that capture has seen a full-update or a state flip for, in
its latest state).

The split is conflict-driven: a group is only gated when its captures actually disagree about what the screen
says (the `remoteformat` `category:id` list — `ConversationVariants.Signature`). Greeting a vendor while an
unrelated quest happens to be active must not make that vendor conditional on it, and nearly every NPC stays
unconditional. Within a group that does disagree:

- The classes are ordered by how much of the player's quest state their captures knew. The least-informed
  class is the unconditional fallback — what a character with no chain progress gets.
- Every other class is gated on a `(quest, state)` pair *all* of its captures agree on and that no
  less-or-equally-informed class carries. Active is preferred over Completed where both would do.
- Two classes the captured state cannot tell apart share one gate, and `ConversationExtractor.Merge`'s
  packet-count tiebreak keeps the richer. That is scoped to the pair — it does not cost the group its other gates.

Gates therefore overlap by construction: a player deep in a chain still holds every earlier quest of it, so
Casilda's escort greeting (`12407` Active) and the two greetings after it all pass at once. The runtime resolves
that by serving the variant gated on the quest the player accepted most recently
(`ConversationService.PickByLatestQuest` — `CharacterQuest` is insert-ordered, so the highest row id is the beat
the player is on), falling back to the unconditional variant when no gate passes.

A gate keyed on capture file names would be authoritative for every NPC it named and silently drop a screen from
any capture nobody listed; derived from the data, Casilda's gates are her in-progress greeting plus four further
chain-stage variants (`12369` / `12373` / `12407`), with the post-freeing beat stated as `12407 Active`.

### The greeting-count gate — the second condition kind

Quest state is not the only thing retail varies a screen on. Six groups it could not split were decrypted, and
four of them are the same pair: a full welcome the first time a player opens the window, and a terse line every
time after — with an *identical* option list on both.

| NPC | first meeting | repeat |
|---|---|---|
| Elethia (`9129`) | `10612` *"{social greet}Hello."* | `10614` *"What now?"* |
| Sancha (`10992`) | `16899` *"Anything for a paying customer."* | `16900` *"Hmm? Yes?"* |
| Dathos (`14477`) | `29083` *"We are all travelers."* | `29085` *"Our paths have crossed."* |
| Ulfheoinn (`21642`) | `58287` *"Well met, warrior."* | `58288` *"There is strength in your eyes, friend."* |

`ConversationEntry.RepeatGreeting` carries it: null on nearly every root (the split is conflict-driven, like the
quest one), false serves only a first meeting, true only a repeat. The runtime holds the set of greeted NPC
templates per session (`NetworkClientSession.GreetedConversationNpcs`) and narrows the candidate roots before the
quest gate runs. `ConversationVariants` only takes the split when the flag separates the group cleanly — two
classes, every capture of one on a first meeting and every capture of the other on a repeat; anything looser is a
correlation and is left to the packet-count tiebreak with the report saying so.

**Best-supported reading, not a decoded fact.** The competing explanation is that retail picks a greeting at
random. What the corpus says, over all 21 sequences where one capture opens an NPC's window more than once:

- a line never recurs once a different one has appeared — 0 of ~12 opportunities. A random draw predicts `A>B>A`.
- 8 NPCs greeted 2–4 times give the same line every time (Turach four times), so re-greeting does not by itself
  reroll anything.
- of the four NPCs above only two are informative: Sancha and Elethia, both `A>B>B`. Dathos and Ulfheoinn were
  opened twice, and with two openings and two lines the first *always* differs, so they say nothing. Two informative
  cases is 9:1 for a counter over a uniform draw — suggestive, not decisive.

The text is what tips it: `{social greet}` is on the first line and absent from the second, and *"What now?"* /
*"Hmm? Yes?"* are not first-contact lines. This model also reproduces every captured sequence exactly, which a
random pick does not. Whether retail's flag is per session or per character for life is likewise undecidable — no
capture has one character meet these NPCs across two dumps — so the runtime holds it per session.

The two groups this does not explain stay reported: Casilda's `0>0>0>0>0` farewell (three variants —
*"Farewell, then."* / *"I'll never forget your help."* / *"Gods go with you."*, plainly chain beats the captured
state cannot separate) and Cavan's greeting, whose two variants differ in their option list too (`6416`
*[HELPING THE SETTLEMENT]* present on one), so its condition is quest availability rather than a greeting count.

### Session slots — regenerated, never stored

A screen carries no dialogue text: `id`+`category` name a string in the CLIENT's LDB and each `<parameter>` fills
one of that template's placeholders. Three of them are session state rather than content, and all three are
regenerated per send by `RemoteFormatSession.WithLivePlayer` from the live character, their guild and the realm:

| parameter | captured value | what we send |
|---|---|---|
| `name` | the capturing player's character | the live character's name (captured `flags` kept — the gender the localized text declines against) |
| `guildname` | their guild, or empty | the live character's guild (empty when guildless), looked up by `Character.GuildId` |
| `npcguildname` | `Not in guild [<retail server>]` | `Not in guild [<our realm's name>]` (`RemoteFormatSession.NotInGuildLabel`) |

The importer therefore stores them blank (`ConversationScreenColumns.ToStoredRow`, applied after `ConversationScreenCodec.Parse`'s
byte-exact gate): the slot stays — the client parses the screen by parameter name and order, and `flags` matters —
but the string goes, so no captured character / guild / retail-server name reaches `faolan.db` or the committed
`faolan.sql` seed. `SeedSessionDataTests` gates that on the shipped seed. `npcguildname` also has a second captured
shape, the `<localized token="StrangerGuild" category="100">` reference; that one is content and is kept verbatim.
Only the guildless label is attested (no captured NPC belongs to a guild), so it is the only one we build.

`AnswerList` (0xF164A4A5) and `CloseChatWindow` (0xC4AF39EB) share one grammar (validated 163/163 +
40/40 across the structured rows): the KnubotBase header then `[u32 count][ count x ([u16 len][remoteformat string]) ]`. CloseChatWindow
is that shape with a single empty `id="0"` remoteformat (retail's "nothing (more) to say").

**Carrier framing (confirmed against the captures):** every Knubot s2c packet is opcode `0x2000`, sender
`GameAgent` (`0xB171CE13`) : `<conversationAgentInstance>`, receiver `GameInterface` (`0x6C67C147`) :
`<playerHandle>`. Retail varies the GameAgent sender instance per conversation — `0x07, 0x08, 0x0a, 0x0b,
0x0e, 0x10, 0x11, 0x54..0x5a` observed (`0x08`/`0x0e` most common) — and the client addresses its `KnubotAnswer`
(c2s: `GameInterface:<player>` → `GameAgent:<thatInstance>`) back to whichever instance sent the screen. So the
construct path preserves the captured sender instance (the `ConversationPacket.SenderInstance` column, decoded
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

A captured conversation node also carries packets that are not Knubot dialogue text (~72 of 511 rows in
`faolan.db`). These are **`DispatchSetDialogueCamera`** — opcode **`0x2015`**, caller **GameCharAgent
(`0xA9ECB95D`)** → receiver **GameCharInterface (`0x8B87F791`)** (a different endpoint pair than the Knubot
dialogue's GameAgent→GameInterface `0x2000`). The body is the cinematic conversation camera's two focus
entities + params:

```
[u32 0xC350][u32 npcHandle][u32 0xC350][u32 playerHandle][u32 0x05010119][trailer]
```

They carry no `remoteformat` text, but they are structured: `ConversationScreenDecoder.Decode`
stores the GameCharAgent sender instance + the param bytes in the `SenderInstance`/`CameraParams`
columns, and the runtime builds the packet from those columns via
`SetDialogueCameraCodec` (framed by `ConversationService.BuildDialogueCameraPacket`) re-addressed to the live player + NPC handles (byte-exact in
`ConversationConstructTests`). The interleaved quest-grant packets are built by the `Codecs/Quest` codecs.

Open work tracked in Gitea #642.
