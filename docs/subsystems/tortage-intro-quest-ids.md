# Tortage intro quest ids (per-class check)

Decoded from the capture set (`Captures/*.tshark.json`) by reading a u32 BE at offset **+17** from each
`QuestIIR` (0x97E02314) / `QuestFullUpdate` (0x63268258) sub-opcode. Codecs: `QuestCodec` /
`QuestFullUpdateCodec`. Note the two shapes differ: `QuestFullUpdateIIR` opens its payload (a `QuestcRecord`)
right after the 17-byte header, and that record starts with `questId`, so the +17 read yields the real quest
id there; `QuestIIR` puts `questId` at **+21** and holds `state` at +17, so the uniform +17 read on a
`QuestIIR` catches the `state` value (2/4), not the id (this is what surfaces the "2, 4" noted below).

## The beach Casilda chain is shared, not per-class

The Tortage beach Casilda chain uses the same quest ids for every class:

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

So the chain (and its triggers) must not be keyed on class. The server drives it data-driven
(`QuestProgressionService`, name-keyed goals).

## Other shared early-beach quest ids (appear in every beach capture)

- **10238** — `QuestContent.BeachStarterQuestId` (the wash-ashore starter, seeded on first beach entry).
- **5744, 5745, 5867** — shared early quests present in every beach login (purpose not pinned; same ids
  for guardian/necro/herald/assassin/conqueror, so also not per-class).
- **2, 4** — not quest ids (framing/state values the decoder's +17 read catches; ignore).

## Post-beach / Tortage city destiny quests (a different id band)

These appear after the beach chain, once the player is heading into / inside Tortage city. They are a separate id
set from the beach chain, not a per-class variant of it — they are the next quests:

- `Hoxxling_…_login_gameplay` (late): **12369**, **12373**
- `Hoxxling_…_login_gameplay2`: **10286** (@idx 5565), **10287**, **14020**, **14014**, **14012**, **13041**,
  **10915**

Whether the city destiny quests vary by class is not determined: only the Hoxxling (herald) playthrough
captures the city, so there is no cross-class city sample. A second class's city run compared against this
10286/10287/14012… set would settle it.

## Destiny Scry steps

`ScryScriptIIR` (0xA36D3B74) named steps from the Hoxxling dumps:
`StartFixed3d, StartScriptGroup(385910), WelcomeTutorial, ApproachCasilda, LeaveCasilda, CombatTutorial, Level,
QuestRewards` (beach), then `ShacklesOff, StartScriptGroup(2012264), GoingInside, GoingOutside` (free/escort).

- `ApproachCasilda`/`LeaveCasilda` are cycling tutorial-camera waypoints (fire repeatedly, before she's
  freed) — not the wall lift. In-client their whole visible effect is a couple of tip **message boxes**, not a
  scripted camera tutorial.

### The beach step tail is event-driven, not a sequence

Corpus census of the beach tail (occurrences / captures carrying it): `ApproachCasilda` 25 / 17,
`CombatTutorial` 12 / 9, `LeaveCasilda` 8 / 8, `Level` 8 / 7, `QuestRewards` 7 / 7. `WelcomeTutorial` (55 / 45)
and `StartFixed3d` (44 / 44) are the intro proper and appear in essentially every beach capture.

The tail is not a fixed order — two captures disagree, so it is driven by what the player does, not by a
script the server plays out:

| capture | order (`--report timeline`) |
|---|---|
| `Hoxxling_…_login_gameplay` | ApproachCasilda → LeaveCasilda → CombatTutorial → Level ×2 → ApproachCasilda → QuestRewards |
| `Blablabla_soldier-guardian_create_…` | CombatTutorial → ApproachCasilda → LeaveCasilda → CombatTutorial → Level → ApproachCasilda → QuestRewards → ApproachCasilda |

No captured c2s packet triggers any of them. Each is a bare server push, and the frames immediately
before it are ordinary interest-stream traffic (`0x2020`, despawns, BCC) — retail is evaluating them from its
own game state (proximity to Casilda, entering combat, level-up, quest turn-in), which the capture does not
carry. So the *step bytes* are fully constructible (`ScryScriptCodec`) but the *trigger conditions* are not
recoverable from the corpus; picking radii and conditions would be invention, not construction.

`--report timeline` decodes these inline (type, step token, zigzag-decoded script group, bound entity), which is
how the orderings above were read off.

### `ShacklesOff`

`ShacklesOff` is the player's manacle removal (the player washes ashore in Slave Manacles), and it happens in
Tortage city, not on the beach. It occurs in only one capture (`Hoxxling_..._login_gameplay2` @5579), and that
whole capture is in the city — it references maps 4020/4022 from the very start and never the beach (4010); the
prior quest grant is the city quest 10286. So it fires after the beach→city transition, and it is not the beach
wall lift: sending it on the beach Casilda-free leaves the wall up in-client. The beach "invisible wall" is not a
standalone "wall down" packet on the beach; the most likely release is the beach→city transition itself (the
destiny confinement ending when the player takes the exit to the city).
