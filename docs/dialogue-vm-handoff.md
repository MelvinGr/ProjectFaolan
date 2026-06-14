# Dialogue VM — handover

**Goal:** make NPC conversations serve a **quest-state-consistent** dialogue tree, so a player only ever
walks the screens valid for their state (pre-offer → offer → accept → in-progress → turn-in → done). Today
the conversation replay works *mechanically* (screens render, options click, a quest grants, the journal
updates) but the **content tree is wrong**: it merges captures from different quest states into one tree, so
a fresh character sees the wrong greeting and an option tree that mixes pre/post-accept screens and dead-ends.

This is the long-pinned "RDB Lua quest VM" (`memory protocol_conversation_routing`, `lua_vm`). Gitea **#37**.

---

## The METHOD that works (learned the hard way 2026-06-14)

**Capture first, no blind edits.** Conversation bugs are invisible from the server side — the server thinks
it served a screen; only the client knows what rendered. Two blind guesses at the conversation flow this
session each made it *worse* (one absorbed a duplicate greeting → client closed; one stuck the window). The
fix that landed (the auto-exit) came from a **loopback capture**:

```
# capture loopback while you drive ONE clean interaction in-client
dumpcap -i lo -f "tcp portrange 7010-7020" -w Captures/<name>.pcapng
#   ... talk to the NPC: open, navigate, close ...
pkill -INT -f dumpcap
bash Captures/tshark_dump.sh Captures/<name>.pcapng     # -> Captures/<name>.tshark.json
```

Then trace the conversation sub-opcodes in frame order (c2s `TalkToNpc/Interact` 0x642CD3D6, `KnubotAnswer`
0x4D7D9123; s2c `OpenChatWindow` 0x045B0B0F, `AppendText` 0x4A3D899A, `AnswerList` 0xF164A4A5,
`CloseChatWindow` 0xC4AF39EB). The reference capture from this session is kept at
**`Captures/casilda_convo.pcapng`** (+ `.tshark.json`). Change conversation code only against wire evidence.

The Knubot packet grammar is decoded (Ghidra): `docs/knubot-conversation-packets.md`.

---

## What is already BUILT (and works)

Runtime (`Faolan/GameServer/Login/ConversationReplay.cs`, partial `GameServerListener`):
- **Replay** — `TryReplayConversation` (greeting on `TalkToNpc` field3=0) → `ServeScreen` (re-addresses the
  captured `ConversationPacket` bytes to the live player/NPC and sends them) → `AdvanceConversation`
  (`KnubotAnswer` index extends the answer-path, serves the child). Un-walked branch → `SendConversationClose`.
- **Condition-gated GREETING variants** — `PickConversationScreen` evaluates each variant's `ConditionScript`
  (Lua, via `LuaScriptVm.RunCondition`) against the live player's tags + quests, serves the first match, else
  the unconditional fallback. **Only the greeting (`AnswerPath == ""`) carries a condition today.**
- **`HasQuest(id)`** (active OR completed) + `HasCompletedQuest`/`HasTag` Lua predicates
  (`GameLuaPlayer`/`ILuaPlayer`/`LuaScriptVm`).
- **Accept-from-dialogue** — `ConversationNode.GrantQuestId` (harvested generically: the node where a
  QuestFullUpdate first appears after an option pick; 15 nodes / 9 NPCs). `ServeScreen` → `GrantQuestFromConversation`
  persists the quest active + pushes its journal content (QuestIIR/QuestFullUpdate/QuestJournal). **This works
  once the tree reaches the grant node.**
- **Close-strip fix (commit 1cc161b)** — a captured screen that offers options sometimes carries a trailing
  `CloseChatWindow`; `ServeScreen` now drops it when the screen has a real `AnswerList`, so the dialogue stays
  open. (Was: greeting opened then instantly closed.)

Importer (`Faolan.Importer/`):
- `ConversationExtractor.Extract` — rebuilds each capture's dialogue as a tree of screens keyed by answer-index
  PATH (`""`, `"0"`, `"0>1"`, …); `GrantQuestId` harvested per screen.
- `ConversationExtractor.Merge` — **unions screens across captures by `(NpcName, TypeId, AnswerPath,
  ConditionScript)`**, keeping the richest. ← this is where the state-mixing happens (see below).
- `ConversationConditions` — the per-`(capture, NPC)` quest-state condition rules (e.g. Casilda's in-progress
  = `HasQuest(13479) and not HasCompletedQuest(13479)`, done = `HasCompletedQuest(13479)`).

DB: `ConversationNode` (NpcName, TypeId, AnswerPath, CapturedInstance, CapturedNpcHandle, ConditionScript,
GrantQuestId) + `ConversationPacket` (Sequence, Data).

---

## What WAS BROKEN (the core problem) — FIXED 2026-06-14, commit `50ad9cc`

Diagnosed in-client + loopback (Casilda, fresh char, quest 13479 not accepted):
1. **The greeting content is wrong** — the served unconditional/fallback variant was NOT the true pre-quest
   first-meeting offer.
2. **The option tree mixes quest states** — "wrong order", options are a mix of pre/post-accept screens.
3. **Dead-ends** — "stuck without options" at the accept node.

Root cause: **the unconditional "offer" fallback was a Frankenstein merge.** Every capture with NO
quest-state rule got an empty `ConditionScript`, so `Merge` (keyed on `(NpcName, TypeId, AnswerPath,
ConditionScript)`) collapsed them all into ONE fallback tree. For Casilda that pulled in a 48-screen
mid-game re-walk (`hoxxling-login-gameplay`, granting *follow-up* quests 13480/12373/12369 at inconsistent
paths) and a loopback of OUR OWN server (`casilda_convo`) — both poisoning the fresh-char greeting.

## The FIX that landed — whole-tree gating + authoritative classification

1. ✅ **Gate the WHOLE tree by state** — `ConversationConditions.Apply` already ran per-capture and tagged
   ALL of a capture's screens (not just the greeting); the gap was unclassified captures defaulting to the
   shared empty-condition fallback.
2. ✅ **Authoritative classification for state-managed NPCs.** An NpcName listed in
   `ConversationConditions.Rules` is now *managed*: its dialogue is drawn ONLY from classified captures; an
   unlisted capture's nodes for that NPC are **dropped** (`Apply` returns nothing), so a mixed-state /
   loopback capture can't pollute the per-state tree. Casilda's offer is now the three clean offer walks
   (randochar / Strie / strickenbla), which agree (`"" → 0 → 0>0 → 0>0>0 → 0>0>0>0` grants 13479) and union
   into one self-consistent tree; in-progress/done stay conditioned. `ConversationNodes` 166 → 139.
   - New tool: **`dotnet run --project Faolan.Importer -- --dump-conversation-states`** prints per-(capture,
     NPC) screen counts + mid-convo quest GRANTS — the read-only "classify captures by state" step. Use it
     to author new gates (a grant at the shallowest path ⇒ that capture is the OFFER walk for that quest).
   - The runtime needed no change: `PickConversationScreen` already serves the offer fallback when every
     conditioned gate fails (a fresh char).

## What REMAINS

1. **In-client navigation verification** (capture-first, the acceptance gate below). The DB tree is now
   clean + self-consistent and 342/342 tests pass, but the offer→accept→journal walk has NOT yet been driven
   in-client since the fix.
2. **Fill state gaps with the RDB Lua VM.** Casilda's follow-up quests (13480, 12369 — dropped with the
   hoxxling capture) and any state no capture covers need the RDB dialogue scripts + the Lua/tag VM
   (`memory lua_vm`: 1992 LuaArchive scripts + MoonSharp + CharacterTag store already built) to drive
   branching instead of replay.
3. **Generalize to more NPCs.** Only Casilda is managed today; classify the other quest-giving NPCs (use
   `--dump-conversation-states`) and add their offer/in-progress/done rules.

Acceptance (in-client): a fresh char talks to Casilda → sees the offer → navigates `0,0,0,0` to accept →
quest 13479 grants → journal updates (grant + journal plumbing already work); re-talk shows in-progress;
complete → done greeting.

---

## Wiring points
- Runtime: `Faolan/GameServer/Login/ConversationReplay.cs` (`PickConversationScreen` = the variant selector;
  extend it to gate every path).
- Importer: `Faolan.Importer/ConversationExtractor.cs` (`Extract`/`Merge`) + `ConversationConditions.cs`
  (whole-tree state conditions) + `Program.cs` (the conversation assembly pipeline).
- VM: `Faolan/GameServer/Scripting/` (`LuaScriptVm`, `GameLuaPlayer`, `ILuaPlayer` — `HasQuest` is there).
- DB: `ConversationNode.ConditionScript` (already exists; just needs values on non-greeting nodes).

## Related
`docs/knubot-conversation-packets.md` (wire grammar), `docs/quest-log-handover.md` (journal, which the grant
feeds), `Captures/casilda_convo.pcapng` (reference capture), `memory protocol_conversation_routing`,
`protocol_quest_state`, `lua_vm`, `rdb_extraction`. Gitea #37.
