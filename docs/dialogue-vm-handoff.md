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

## What is BROKEN (the core problem)

Diagnosed in-client + loopback (Casilda, fresh char, quest 13479 not accepted):
1. **The greeting content is wrong** — the served unconditional/fallback variant (the randochar capture) is
   NOT the true pre-quest first-meeting offer.
2. **The option tree mixes quest states** — "wrong order", options are a mix of pre/post-accept screens.
3. **Dead-ends** — "stuck without options" at the accept node.

Root cause: **only the greeting is state-gated; the deeper nodes are state-blind.** `Merge` dedups children
across captures by `(NpcName, TypeId, AnswerPath, ConditionScript)`, and the children have an **empty**
condition — so a `"0>1"` screen captured post-accept and one captured pre-accept collapse into ONE node, and
whichever scored higher wins regardless of the walking player's state. The result is a single Frankenstein
tree. Plus the captures may not cleanly contain every state (the true pre-quest offer may be absent).

---

## The FIX (per-quest-state dialogue trees)

1. **Gate the WHOLE tree by state, not just the greeting.** Tag every `ConversationNode` (not only the
   greeting) with the quest-state condition of the capture it came from, and have `PickConversationScreen`
   serve the variant matching the live player at EVERY path — so the player walks a single capture's
   self-consistent subtree. Concretely: in the importer, propagate the per-`(capture, NPC)` condition
   (`ConversationConditions`) onto ALL of that capture's screens for that NPC, not just the greeting, and add
   the condition to keep them as separate rows through `Merge`. (Today `ConversationConditions.Apply` already
   runs per-capture; extend it from greeting-only to whole-tree, and make `DeriveOfferConditions`-style
   gating per node.)
2. **Classify captures by state.** For each (capture, NPC), determine the quest state it represents
   (pre-offer / offer / in-progress / complete) from the capture's quest packets (a QuestFullUpdate *grant*
   during the convo ⇒ this capture is the OFFER state; an active QuestIIR at login with no grant ⇒
   in-progress; HasCompletedQuest ⇒ done; none ⇒ pre-offer). Use that to author the per-node condition.
3. **Fill state gaps with the RDB Lua VM.** Where no capture covers a state (likely the true pre-quest
   offer), fall back to the RDB dialogue scripts + the Lua/tag VM (`memory lua_vm`: 1992 LuaArchive scripts +
   MoonSharp + CharacterTag store already built) to drive branching instead of replay.

Verification is **in-client only** (capture-first). Acceptance: a fresh char talks to Casilda → sees the
offer → can navigate to accept → quest 13479 grants → journal updates (the grant + journal plumbing already
work); re-talk shows in-progress; complete → done greeting.

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
