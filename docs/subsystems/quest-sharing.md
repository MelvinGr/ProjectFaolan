# Quest sharing

Offering a quest you are on to your team-mates, and accepting one offered to you (Gitea #101). It is the one
two-player subsystem in the server that **no capture performs** — teams, trade and the friend list all came off
`login_2-clients_group-trade-friend`, but neither client in it ever shares a quest.

So every byte here is recovered from `AgeOfConan.exe` instead: each dispatch's field widths come from the client's
own reader, and each RULE the server applies comes from the client code that applies the matching one. Nothing is
gated against retail bytes, and nothing here claims to be — the tags below are `[V0]` for exactly that reason.

## The messages

c2s, `GameCharAgentMethods` — all four raised by the journal's Share button or the share window:

```
0x204F ShareQuest(const ID32& i_cQuestId)             [u32 class][u32 questId]   - the instance is the quest id
0x2050 ShareQuestDeclined(const ID32& i_cRequesterId) [u32 class][u32 characterId]
0x2051 ShareQuestAccepted(const InstanceID_t& i_cQuestId)  [u32 questId]         - ONE u32, not an ID32
0x2052 ShareQuestBusy(const ID32& i_cRequesterId)     [u32 class][u32 characterId]
0x2054 RequestTeamQuestList()                          (empty; still unanswered - see below)
```

`InstanceID_t` being one u32 is the client's own proxy saying so: `GameCharAgentProxy_t::ShareQuestAccepted`
(`FUN_00500960`) loads a single dword out of the argument, byte-swaps it inline and streams **4** bytes, where the
ID32 writer streams 8. [V0: the quest-share message bodies]

s2c, `GameCharInterfaceMethods`:

```
0x2048 DispatchQuestShareGlobalFeedback(i_nFeedbackType, i_nFeedbackValue)      [u32][u32]
0x2049 DispatchQuestShareIndividualFeedback(i_nFeedbackType, i_cFeedbackValue)  [u32][u16 len][LATIN1]
0x204A DispatchQuestShareRequest(i_cCharID, i_nStartTaskTemplate, i_cQuestName) [ID32][u32][u16 len][LATIN1]
0x204B DispatchUpdateTeamQuestList(cQuestData)                                  (one opaque object - not built)
0x204C DispatchCancelTeamQuestList(nQuestShareCode)                             [u32]
```

Each layout is read straight off `GameCharInterfaceDispatch_t`, whose per-field bail-outs name the field they were
about to read (*"stream bad after 'i_nStartTaskTemplate'"*), so the order is not inferred:

| method | reader | reads |
| --- | --- | --- |
| `DispatchQuestShareRequest` | `FUN_004c5bf0` | u32, u32 (one ID32) → u32 → string |
| `DispatchQuestShareGlobalFeedback` | `FUN_004c58f0` | u32 → u32 |
| `DispatchQuestShareIndividualFeedback` | `FUN_004c5a50` | u32 → string |
| `DispatchCancelTeamQuestList` | `FUN_004c5fa0` | u32 |

⚠️ **`i_cFeedbackValue` is a STRING, not an ID32**, despite the `i_c` prefix that marks an object everywhere else.
`FUN_004c5a50` calls the string primitive (`FUN_0048f840`) — the same one `i_cQuestName` uses — and never the id
reader. The handler substitutes it into the message as the player NAME parameter, which is what it is for.
[V0: the quest-share message bodies]

## The feedback codes

The two feedback methods do not carry two enums. They carry two **disjoint slices of one 0..11 space**, and which
method a code travels on is fixed by the code. Both halves are jump tables in `GamecodeClient_t` that resolve an LDB
message key (category 300) and log `Invalid feedback type (n)` for anything outside their own slice — so the set is
exhaustive, not a sample.

| code | method | client message key | value |
| --- | --- | --- | --- |
| 0 | global | `ShareFeedbackNotInTeam` | — |
| 1 | global | `ShareFeedbackMainQuestNotFound` | — |
| 2 | global | `ShareFeedbackMainQuestCanNotShare` | — |
| 3 | individual | `ShareFeedbackLevelTooLow` | player name |
| 4 | individual | `ShareFeedbackLevelTooHigh` | player name |
| 5 | global | `ShareFeedbackLegitTargetsOne` / `…Many` | count |
| 6 | global | `ShareFeedbackOutOfRangeTargetsOne` / `…Many` | count |
| 7 | global | `ShareFeedbackNoLegitTargets` | — |
| 8 | individual | `ShareFeedbackQuestDeclined` | player name |
| 9 | individual | `ShareFeedbackPlayerBusy` | player name |
| 10 | individual | `ShareFeedbackAlreadyFinished` | player name |
| 11 | individual | `ShareFeedbackDoesNotHaveExpansion` | player name |

Codes 5 and 6 are the reason the global value is a **count** rather than a flag: the client picks the `…One` key when
the value is exactly 1 and the `…Many` key otherwise, substituting the number into the latter.

Recovered from `FUN_01396ef0` (jump table @`0x13970bc`, accepts 0,1,2,5,6,7) and `FUN_013970e0` (jump table
@`0x1397258`, accepts 3,4,8,9,10,11), with the message-key operands at `0x207a944`…`0x207ab18`.
[V0: the quest-share feedback codes]

## The share window, and the rules it implies

`ControlCenterModule_c::SlotQuestShareRequest` (`FUN_01220630`) builds the window from
`Views/QuestShareGUI/QuestShareView.xml`, keeps the sharer's ID32 and binds `i_nStartTaskTemplate` as the accept
button's 4-byte payload — which is what makes `ShareQuestAccepted` an echo of that same number and nothing else.

Its per-second tick (`FUN_01220bb0`) is where two server rules come from:

* it resolves the sharer (checking the ID32's class word against 50000 = `EntityClass.Character`) and measures the
  distance every tick, dropping the window with `QuestSharePlayerMovedOutOfRange` past **60.0 units**;
* it counts down and closes with `QuestShareTimeOut` when it runs out.

So the server's range test is the client's own leash: offering a quest to someone further away raises a window that
immediately cancels itself. `QuestSharingService.ShareRadius` is that 60. The TIMEOUT needs nothing server-side —
the client closes its own window, and a stale offer is dropped when the target disconnects.
[V0: the quest-share range and timeout]

## What the server does

`QuestSharingService` (`Faolan/GameServer/Quest/`) owns the rules; it reaches the team roster through
`ICsPlayerAgentProvider.TeamMembersOf` (the roster lives on the CS tier, the share arrives on the game connection)
and the wire through `IQuestShareOutbound`, the same narrow-port shape `ITradeOutbound` takes.

On `ShareQuest`:

1. no team → `NotInTeam`;
2. the sharer is not on that quest, or it has no definition → `MainQuestNotFound`;
3. `QuestDefinition.QuestLineFlags` bit 4 (0x10) set → `MainQuestCanNotShare`. That is the same bit that greys the
   journal's own Share button out — proven in-client, see the column's note;
4. otherwise, per team-mate: already completed it → individual `AlreadyFinished`; already on it → not a target and
   nothing is said (the twelve codes have no case for it); out of the 60-unit leash or on another playfield → counted
   for `OutOfRangeTargets`; else the `DispatchQuestShareRequest` goes out;
5. then the counts: `LegitTargets` and/or `OutOfRangeTargets`, or `NoLegitTargets` when neither.

On `ShareQuestAccepted` the quest granted is the one **we** offered, taken from the server's own pending map — the
body's echoed id is only checked against it, so a forged accept cannot hand its sender an arbitrary quest. The grant
itself is `QuestProgressionService.Grant` + `SendGrantPackets`, the same pair the dialogue path uses. Retail has no
"accepted" feedback code, so the sharer is told nothing; `ShareQuestDeclined` / `ShareQuestBusy` relay
`QuestDeclined` / `PlayerBusy` naming the decliner.

`QuestSharingServiceTests` gates all of it. `QuestShareCodecTests` pins each body's widths and order against the
client's reader.

## Not built

* **`LevelTooLow` / `LevelTooHigh` (codes 3, 4).** The codes are known; the level BAND they compare against is not —
  no field of `QuestDefinition` states a min/max, and inventing one would be a rule of ours wearing retail's message.
  A quest is offered regardless of the target's level until that band is sourced.
* **`DoesNotHaveExpansion` (code 11).** We model no per-account expansion entitlement.
* **`DispatchUpdateTeamQuestList` (0x204B) and `DispatchCancelTeamQuestList` (0x204C).** `RequestTeamQuestList`
  (0x2054) is still consumed with no reply: 0x204B's single field is one opaque object with no capture to shape it,
  and an empty list is a claim we cannot make byte-exactly. 0x204C's body IS decoded (one u32) but the share CODES it
  carries are not, so there is nothing to put in it.

Open work tracked in Gitea #101.
