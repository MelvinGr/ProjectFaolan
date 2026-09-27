# World-entry performance: where the entry spends its time

A world entry (`WorldEntryService.SendBurstlessWorldEntry`) is the single heaviest thing the game host does per
player. Every real entry logs its own step breakdown, so the cost is attributed rather than guessed at:

```
Burst-less world entry (map=4010, 'Name'): sent 19 constructed packets (...) - total 7321 ms: completed-journal 2583, npc-followups 1741, zone-npcs 1015, ...
```

`--filter-class '*WorldEntryProfile*'` reruns it in-process against a copy of this checkout's `faolan.db`, with no
client and no login:

```zsh
FAOLAN_PROFILE_ENTRY=<characterId>[,<characterId>] dotnet test --project Faolan.Tests -- --filter-class '*WorldEntryProfile*'
```

The first character listed pays every once-per-server and once-per-map cost; a second on the same map pays only
its own rows. That difference is the whole point of passing two.

## Three lifetimes, three fixes

The total mixes costs that recur on different schedules, and each wants a different remedy:

| lifetime | what | fix |
|---|---|---|
| once per server | the combat catalogs (`BuffStatEffects` ~400 ms, `SpellPoolEffects`, `SpellEffectService`, `ComboService`) and the quest-giver / goal-target maps | warmed at startup — `RealmWarmupService` |
| once per map | the zone's NPC load (`EnsureZoneNpcs`, ~1 s cold) and its terrain heightmap + collision (~130 ms) | terrain is warmed off the tick at entry; the zone load stays lazy (see below) |
| every entry | the character's own rows — the completed journal, the per-NPC quest markers, the snapshot | query work shaped to what each consumer reads |

## `RealmWarmupService` — the once-per-server half

Each combat catalog and each quest map is a lazily built singleton behind its own load lock. Correct as a lazy
load, but it means whoever first casts, is first hit, or first walks up to a quest giver pays the whole build
inside their entry. These costs are once per process and shared by every map, so `RealmWarmupService` builds
them at startup (fire-and-forget on `ApplicationStarted`, as `RealmStatusService` flips Online). A warm that
throws is logged and swallowed — the lazy load behind each still runs on first use, so a failed warm degrades to
a slower first use rather than breaking the server.

It deliberately does not warm per-map state. A zone's NPCs and terrain are once-per-map, not
once-per-server, and eagerly loading every map's zone at startup would build worlds no one has entered and take
instance ownership for a player who may never arrive. The zone load stays lazy; only the entered map's
terrain is warmed, off the tick, at entry.

## The dominant costs are per-character, not per-map

The two biggest slices for a level-80 with a full journal — `completed-journal` and `npc-followups` — are paid on
every login by that character, not just the first into a map, and no preload touches them. Both are kept off the
N+1 shape by reading exactly what the consumer renders:

**`completed-journal`.** `LoadCompletedJournal` reads through `GetCompletedJournal`, not the active journal's
`GetQuestDefinitions`: that read's five-level Include (goals → regions → vertices, reward options, the line's
rewards) exists for the active journal, and pulling it for every quest the character ever finished (plus all 2,210
quest lines) cost 2745 ms for a level-80 with 1,565 completions. The completed tab renders a title and a sub-line
per quest under a line's three names and nothing else, so `GetCompletedJournal` reads the definitions with only
their `TextRefs`, and only the lines those quests sit on: ~160 ms, with the snapshot packet that carries the tab
byte-identical (hashed by the profiler's `FAOLAN_PROFILE_DUMP`).

**`npc-followups`.** `QuestMarkerResolver.MarkerStateFor` resolves against a `QuestMarkerContext` — the character's
quest state, read once per batch (the entry followups, the interest sweep, a spawn poll) and handed to every
resolution in it. What a marker needs from a definition — line, level, step — comes through the
`GetQuestLineMembership` projection, memoised as the NPCs name new quests. Reloading the state per NPC (including,
inside `ChainRunsThrough`, one five-level definition load per quest ever held) ran ~11,000 queries for a burst of
seven NPCs on the level-80 — 3895 ms against ~140–260 ms with the context (26 → 2 ms for a level-4), every marker
packet hashing identically.

## The spawn-poll storm after the loading screen

The entry burst is not the last thing a player waits on. The client sends one `UpdateQuestMarkerNPC` poll
(`0x2020`) per NPC it loads and will not advance until each is answered, so the reply cost is multiplied by the
NPC count and paid serially. A teleport into Tortage (map 4020, 192 NPCs) draws 52 polls in 14 s; at ~95 ms per
reply the player feels a lag spike after the world has appeared.

So the context is cached per online character (`QuestMarkerResolver.ContextFor`), dropped by every writer of quest
state (`IQuestOutbound.ForgetQuestMarkers` — the progression service, the reward pick, the dialogue completion, the
DeleteQuest echo) and on disconnect, with a ten-second creation TTL as a bound on any writer the list does not know
about. Each `0x2020 summary` log line carries `handled in N ms`, so the per-poll cost is visible live.

## `ClearCasildaCaptivePose`

The post-ready block between `GrantCharacterCombos` and `Sent the post-0x2024 login records` has its own step
timer. Its `casilda-pose` step asks "is Casilda still captive for this character", which
`TortageIntroService.EscorteeTemplateId` answers with one projection over the goal rows (`GetEscorteeTemplateId`:
Active/Completed held quests joined to Escort goals, first by quest id then goal id). Walking every quest the
character ever held with the five-level Include to find one Escort goal's `TrailingRefId` cost 4.3–4.9 s on a
level-80 on every world entry, for a character who had never been near the beach.

A slow step here reads as lag rather than a longer loading screen: the client's spawn-poll storm queues behind
this block, so the world is visible but nothing in it responds.
