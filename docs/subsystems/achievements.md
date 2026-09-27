# Achievements (TAGS) — `DispatchAddTag`

An achievement is awarded by one message with one field:

```
s2c  GameCharInterface.DispatchAddTag   [u32 tagId]
```

`GameCharInterfaceMethods.DispatchAddTag = 0x16`. **23 awards over 14 captures** — the whole corpus, found with
`--report method-census --report-arg Tag`.

A remove twin exists in the client's method table, `DispatchRemoveTag` (0x17), with zero captured frames, so
nothing is built for it.

## The id is a TAG, and two different things are tagged

The id space is shared, which is why the message alone does not say what was awarded:

| kind | how it resolves | example |
|---|---|---|
| **Achievement** | LDB **55701** = the title, **55700** = what earns it | 7917 → *Seeing Red* / *"Achieve a kill streak."* |
| **Item-shop "bundle shown" flag** | nowhere in 55701; the client's own shop Lua gates on `not player:HasTag(X)` | 6617 |

**Thirteen of the fourteen captured tag ids resolve as achievements**, and each matches what its capture was doing —
which is what makes the keying a decode rather than a coincidence:

| tag | achievement | the capture |
|---:|---|---|
| 716 | *Old Tarantia Docks* | `annotated_2` t+285.5, the instant the vista fly-through ended |
| 3509 | *Pet Collector* — *"Collect and summon your first pet, companion or mini-pet."* | `..._login_summon_pet`, at the summon |
| 3546 | *Uncommon Pet* | the same instant |
| 2562 / 2563 | *Spider Caverns* — *"Defeat the Lurking Horror in the Spider Caverns"* / *Lurking Horror* | awarded together |
| 3305 / 3286 | *Rare Mount* / *Siege Mount* | mount summons |
| 7917 | *Seeing Red* | six separate captures — the most-awarded tag in the corpus |

The fourteenth is **6617**, which resolves to nothing in 55701; it is one of the item-shop `HasTag` gates recovered
from the client's shop scripts (6617–6620, 7316, 7416–7421, `WorldEntryService`). So the corpus confirms both the id
and its kind.

The catalog carries internal entries beside the real ones (3104 *"XX Dungeon Level Range"*, 3316
*"XX Mammoth - Blue"*), and about three quarters ship no description (3689 *"Puma"* has none). Both are the
client's data as shipped; a row existing does not mean the client shows it to a player.

## Storage

`Achievements` (4,095 rows) is the catalog — id, name, description — imported by **`rdb-achievements`** from the
English LDB pages, chosen by the client's own language records rather than by scoring the prose. `CharacterTags`
records which tags a character holds.

## Delivery: there is no login re-send

None of the 23 captured awards lands in a login burst. Nothing needs to: the client learns the set a
character already holds from the **FullChar snapshot**, which unions `GetCharacterTags` in
(`SendBurstlessWorldEntry`). So `DispatchAddTag` is strictly the moment-of-award event — and a tag the character
already has must not be re-sent, or the client pops its banner again. `GrantTag` persists first and only sends when
the row was new.

## What is not wired: the triggers

Each achievement's trigger is per-achievement content nobody has mapped — a vista, a kill streak, a first pet, a
dungeon boss. So the award path is exercised by the GM command `.tag <id>` / `.tag <name substring>` (and `.tag`
alone lists what the character holds), which is the standard bootstrap for a feature whose retail trigger is not
wired yet. Two of the trigger families are close to reachable, since we have both subsystems: the pet-summon
achievements (3509 / 3546) and the mount ones (3305 / 3286).

Open work tracked in Gitea #748 (the pet-summon path in #307).
