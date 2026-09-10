# World-entry performance: where the entry spends its time

A world entry (`WorldEntryService.SendBurstlessWorldEntry`) is the single heaviest thing the game host does per
player. Every real entry now logs its own step breakdown, so the cost is attributed rather than guessed at
(Gitea #938):

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
| **once per server** | the combat catalogs (`BuffStatEffects` ~400 ms, `SpellPoolEffects`, `SpellEffectService`, `ComboService`) and the quest-giver / goal-target maps | **warmed at startup** — `RealmWarmupService` |
| **once per map** | the zone's NPC load (`EnsureZoneNpcs`, ~1 s cold) and its terrain heightmap + collision (~130 ms) | terrain is warmed off the tick at entry; the zone load stays lazy (see below) |
| **every entry** | the character's own rows — the completed journal (#938, fixed), the per-NPC quest markers (#939, fixed), the snapshot | query work shaped to what each consumer reads |

## `RealmWarmupService` — the once-per-server half

Each combat catalog and each quest map is a lazily built singleton behind its own load lock. Correct as a lazy
load, but it means whoever first casts, is first hit, or first walks up to a quest giver pays the whole build
inside their entry. These costs are once per process and shared by every map, so `RealmWarmupService` builds
them at startup (fire-and-forget on `ApplicationStarted`, as `RealmStatusService` flips Online). A warm that
throws is logged and swallowed — the lazy load behind each still runs on first use, so a failed warm degrades to
the old behaviour rather than breaking the server.

⚠️ It deliberately does **not** warm per-map state. A zone's NPCs and terrain are once-per-map, not
once-per-server, and eagerly loading every map's zone at startup would build worlds no one has entered and take
instance ownership (#268) for a player who may never arrive. The zone load stays lazy; only the entered map's
terrain is warmed, off the tick, at entry.

## The dominant costs are per-CHARACTER, not per-map

⭐ The two biggest slices for a level-80 with a full journal — `completed-journal` (~2.6 s) and `npc-followups`
(~1.7 s) — are paid on **every** login by that character, not just the first into a map, and no preload touches
them. They are N+1-shaped query work of the kind #582 fixed elsewhere.

**`completed-journal` — fixed (#938).** `LoadCompletedJournal` used the ACTIVE journal's read, `GetQuestDefinitions`,
whose five-level Include (goals → regions → vertices, reward options, the line's rewards) exists because the active
journal needs it — and pulled it for every quest the character had ever finished, plus all 2,210 quest lines with
their rewards, unfiltered. The completed tab renders a title and a sub-line per quest under a line's three names
and nothing else, so `GetCompletedJournal` reads exactly that: the definitions with only their `TextRefs`, and only
the lines those quests sit on. **2745 ms → ~160 ms** for the level-80 (1,565 completions), and the snapshot packet
that carries the tab is byte-identical before and after (hashed by the profiler's `FAOLAN_PROFILE_DUMP`).

**`npc-followups` — fixed (#939).** `QuestMarkerResolver.MarkerStateFor` reloaded the character's quest state for
every NPC it was asked about — the rows, the active definitions, and, inside `ChainRunsThrough`, one five-level
definition load per quest the character had EVER held, to read that definition's line and level. A burst of seven
NPCs on a level-80 with 1,565 completions ran ~11,000 queries. The state is now a `QuestMarkerContext`, read once
per character per batch (the entry followups, the interest sweep, a spawn poll) and handed to every resolution in
it; what a marker needs from a definition — line, level, step — comes through the `GetQuestLineMembership`
projection, memoised as the NPCs name new quests. **3895 ms → ~140–260 ms** for the level-80, 26 → 2 ms for the
level-4, and every marker packet in the burst hashes identically before and after.

## The spawn-poll storm after the loading screen

The entry burst is not the last thing a player waits on. The client sends one `UpdateQuestMarkerNPC` poll
(`0x2020`) **per NPC it loads** and will not advance until each is answered, so the reply cost is multiplied by
the NPC count and paid serially. A teleport into Tortage (map 4020, 192 NPCs) drew **52 polls in 14 s** at a
steady ~95 ms each — reply, 2 ms, next poll — which the player feels as a lag spike *after* the world has
appeared. It is #582's shape on the marker path, and it came back the moment each poll built its own
`QuestMarkerContext`.

The context is now cached per online character (`QuestMarkerResolver.ContextFor`), dropped by every writer of
quest state (`IQuestOutbound.ForgetQuestMarkers` — the progression service, the reward pick, the dialogue
completion, the DeleteQuest echo) and on disconnect, with a ten-second creation TTL as a bound on any writer the
list does not know about. Each `0x2020 summary` log line now carries `handled in N ms`, so the per-poll cost is
visible live.

## The 4.6 s between the combos grant and the post-ready records — `ClearCasildaCaptivePose` (#940)

The same teleport showed 4.6 s of near-silence between `GrantCharacterCombos` and `Sent the post-0x2024 login
records`, with five packets on the wire. Two readings were open — our reads, or the client not draining its socket
while it loads the map — and the block's new step timer settled it on the first re-run: `casilda-pose 4914 ms` on
login, `4306 ms` on the teleport, everything else under 100 ms. Ours.

`TortageIntroService.EscorteeTemplateId` answered "is Casilda still captive for this character" by loading EVERY
quest the character had ever held with the five-level Include and walking their goals for one Escort goal — on a
level-80, 1,565 graph loads to read one `TrailingRefId`, on every world entry, for a character who has never
been near the beach. It is one projection over the goal rows now (`GetEscorteeTemplateId`: Active/Completed
held quests joined to Escort goals, first by quest id then goal id — the same order the walk had).

⚠️ Why it felt like lag rather than a longer loading screen: the client's spawn-poll storm queues behind this
block, so the first poll was answered at 21:26:05.7 and the second at 21:26:10.4. The world was visible; nothing
in it responded.
