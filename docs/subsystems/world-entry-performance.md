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
| **every entry** | the character's own rows — the completed journal (#938, fixed), the per-NPC quest markers (#939), the snapshot | query work shaped to what each consumer reads |

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

**`npc-followups` — open (#939).** `SendEntryNpcFollowups` re-reads the character's quests + definitions once per
burst NPC through `MarkerStateFor`; it is now the largest step of a level-80 entry.
