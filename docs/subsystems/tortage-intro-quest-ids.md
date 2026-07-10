# Tortage intro quest ids (per-class check)

Decoded from the capture set (`Captures/*.tshark.json`) by reading a u32 BE at offset **+17** from each
`QuestIIR` (0x97E02314) / `QuestFullUpdate` (0x63268258) sub-opcode. Builders: `WorldQuest.BuildQuestIir` /
`BuildQuestFullUpdate`. Note the two shapes differ: `BuildQuestFullUpdate` puts `questId` at +17
(`WorldQuest.QuestFullUpdateContentOffset = 17`), so the +17 read yields the real quest id there;
`BuildQuestIir` puts `questId` at **+21** and holds `state` at +17, so the uniform +17 read on a `QuestIIR`
catches the `state` value (2/4), not the id (this is what surfaces the "2, 4" noted below).

## Conclusion: the beach Casilda chain is SHARED, not per-class

The Tortage **beach** Casilda chain is the **same quest ids for every class**:

| Stage | Quest id | Goal |
|------:|---------|------|
| 1 | **13479** | kill Scavenger Tuut |
| 2 | **13563** | loot the key from Tuut's bag |
| 3 | **13480** | talk to / free Casilda |
| 4 | **12407** | escort Casilda to the city |

Verified across all captures that reach the chain — **3 archetypes, 5 classes**:

| Capture | Class (archetype) | Chain ids present |
|---|---|---|
| `Blablabla_soldier-guardian_create_random-actions…` | Guardian (soldier) | 13479, 13563, 13480, 12407 |
| `Blablatest_mage-necromancer-create_casilda_quest_first_…` | Necromancer (mage) | 13479, 13563, 13480, 12407 |
| `Hoxxling_mage-heraldofxotli_login_gameplay` | Herald of Xotli (mage) | 13479, 13563, 13480, 12407 |
| `Strie_rogue-assassin_login_randomActions` | Assassin (rogue) | 13479, 13563, 13480, 12407 |
| `Stihetfv_soldier-conqueror_login_…compeltefirstquest` | Conqueror (soldier) | 13480, 13563, 12407 (capture starts mid-chain) |

⟹ **Do NOT key the chain (or its triggers) on class.** It is one shared chain. The server already drives it
data-driven (`QuestProgressionService`, name-keyed goals), which is correct.

## Other shared early-beach quest ids (appear in every beach capture)

- **10238** — `WorldQuest.BeachStarterQuestId` (the wash-ashore starter, seeded on first beach entry).
- **5744, 5745, 5867** — shared early quests present in every beach login (purpose not yet pinned; same ids
  for guardian/necro/herald/assassin/conqueror, so also not per-class).
- **2, 4** — not quest ids (framing/state values the decoder's +17 read catches; ignore).

## Post-beach / Tortage city destiny quests (DIFFERENT id band)

These appear AFTER the beach chain, once the player is heading into / inside Tortage city. They are a separate id
set from the beach chain — this is what was briefly mis-read as "the chain is per-class" (it is not; these are
just the *next* quests):

- `Hoxxling_…_login_gameplay` (late): **12369**, **12373**
- `Hoxxling_…_login_gameplay2`: **10286** (@idx 5565), **10287**, **14020**, **14014**, **14012**, **13041**,
  **10915**

Whether the *city* destiny quests vary by class is **not determined** — only the Hoxxling (herald) playthrough
captures the city, so there's no cross-class city sample yet. If that matters later, capture a second class's
city run and compare this 10286/10287/14012… set.

## Destiny Scry steps (related; see also [[casilda_destiny_scry_steps]])

`ScryScriptIIR` (0xA36D3B74) named steps from the Hoxxling dumps:
`StartFixed3d, StartScriptGroup(385910), WelcomeTutorial, ApproachCasilda, LeaveCasilda, CombatTutorial, Level,
QuestRewards` (beach), then `ShacklesOff, StartScriptGroup(2012264), GoingInside, GoingOutside` (free/escort).

- `ApproachCasilda`/`LeaveCasilda` are **cycling tutorial-camera waypoints** (fire repeatedly, before she's
  freed) — NOT the wall lift.
- **`ShacklesOff`** is the **PLAYER's** manacle removal (the player washes ashore in Slave Manacles, #220), and it
  happens in **Tortage CITY, not on the beach**. PROVEN: `ShacklesOff` occurs in only one capture
  (`Hoxxling_..._login_gameplay2` @5579), and that whole capture is in the city — it references maps 4020/4022 from
  the very start and NEVER the beach (4010); the prior quest grant is the city quest 10286. So it fires AFTER the
  beach→city transition. It is **NOT** the beach wall lift (a city packet can't lift a beach wall). The earlier
  #267 wiring that sent `ShacklesOff` on the beach Casilda-free was WRONG and was reverted (in-client: the wall
  stayed up). The beach "invisible wall" is still unsolved — most likely the beach→city transition itself (the
  destiny confinement releases when you take the exit to the city), which overlaps with #262 (escort to city); it
  is not a standalone "wall down" packet on the beach.
