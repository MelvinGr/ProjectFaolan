# Combos & Spellweaving

Two sibling mechanics — both build up an outcome from a series of player inputs — but they differ in a key
way: melee combos MATCH a fixed catalog sequence, while spellweaving is FREE-FORM composition.

| | Melee combo | Spellweaving (caster) |
|---|---|---|
| Structure | a FIXED sequence from the catalog (`Combos.Actions`) | FREE-FORM: the player composes the weave live |
| Input kind | directional swing key (Upper-Left/Right, Lower-Left/Right, Up, Head, Thrust) | adding a spell "component"; each contributes **extra stats / an effect** to a blended result |
| Timing | rapid succession (chain the keys) | paced adds while in a trance |
| Outcome | the specific combo's finisher (damage / effect) | the rung buff the weave leaves running — there is no fire |
| Risk | — | **backlash**: one roll per rung, and the escape band shrinks to zero by rung 6 |
| Who | the 8 combo archetypes (soldiers, rogues, Bear Shaman, Herald of Xotli) | the pure casters (Necromancer, Demonologist, Priest of Mitra, Tempest of Set) |

A class does **one or the other**. That is why `ClassCombo` (melee) leaves the 4 pure casters empty — they
weave instead. (Bear Shaman & Herald of Xotli are melee/hybrid, so they have `Combos`.)

**Spellweaving mechanic:** activate the spellweave ability → enter a **trance** (immobilised, can ONLY add
components) → add spell "components" one at a time, each advancing the weave one rung and rolling for a
backlash → **deactivate** to leave the trance. Because it is player-composed there is no fixed catalog to
match (unlike melee).

⚠️ **There is no separate FIRE payload.** The weave's whole reward is the `Spellweaving (N)` rung buff it
leaves running, and that buff's DURATION is the reward window — 12 s at rung 1, rising to 32 s at rung 5.
Deactivating ends the trance and lets the rung tick down; it does not launch anything. #141 spent two months
looking for the packet that fires the weave, and the answer is that retail sends none.

### ⭐ The per-class ruleset, from the CLIENT (#141)

`--report spellweave` re-derives everything below in one command; `SpellweaveCatalog` is the shipped
transcription and `SpellweaveCodecTests` gates its invariants.

The table was never capture-borne. The client declares it as the argument list of a **`StartSpellWeavingMode`
(53190)** GameAction element on every rung buff, whose five keys are `NanoTemplate, Arg1..Arg4`
([`../re/gameaction-keylists.tsv`](../re/gameaction-keylists.tsv)). It stayed invisible for as long as it did
because nothing could SEARCH for it — a value search needs an id you already have, and the ids were the
unknown. `--report spell-refs --report-arg kind:53190` searches by element **kind** instead and finds all 24
carriers at once.

**The elements.** Five per class, and the `componentIndex` on a `CombatReactiveIIR` add indexes this list in
order. The first two are universal; only the last three are class-specific.

| class | spell line | elements 0–1 | elements 2–4 |
|---|---|---|---|
| Necromancer | `41xxx` | Arcane Renewal, Arcane Surge | Parasite Host, Death God, Death |
| Demonologist | `44xxx` | " | Hell, Hand of Doom, Howl of Armageddon |
| Tempest of Set | `28xxx` | " | Set, Power Surge, Vengeful Idol of Set |
| Priest of Mitra | `24xxx` | " | Mitra's Grace, Mitra, Benevolence of Mitra |

⭐ The **Necromancer row is the control**: it is element-for-element and index-for-index what the NecroL80
capture wove (componentIndex 0/1/3 → Arcane Renewal / Arcane Surge / Death God), so the other three rows come
off a table already proven right where we can check it. And the class is stated **mechanically**, not read
off the names — each rung's `SpellLineStatModifier` elements name the class's own spell lines, and a line is
`ConanClass * 1000 + index`. ⚠️ Not every leading block is a class: the Demonologist ladder also carries a
`50000` line that resolves to none, the same trap the archetype-shared combos set with their `20` block.

**The ladder.** Six buffs, `Spellweaving (1)..(6)`. Adding a component advances one rung. Each rung grants
`Modify BaseSpellDamage` of `rung × step` (250 for the Necromancer, 100 for the other three), a
`Modify NaturalEnergyRegeneration` of **−15000**, and a splash package on the class's spell lines
(`SplashDamageAOESize` / `SplashDamagePercentage` / `SplashDamageMaxTargets`). Rung 6 is the top BUFF but not
the top of the ramp — its record carries the whole `BaseSpellDamage` continuation for rungs 6..25 in one
list, so a longer weave keeps scaling without a further buff id.

⚠️ **The stack is an ordered LADDER, not a base plus an offset.** The captured necro stack walks
`0x37E5A8 → A9 → AA`, which reads as "the same buff re-applied at an incremented id" and was modelled that
way for months. Those three ids are `Spellweaving (1)`, `(2)` and `(3)` — the stack LEVEL advancing. It only
looked like arithmetic because the necromancer's six rungs happen to be contiguous; the Demonologist's run
DESCENDING and the Priest of Mitra's are interleaved with unrelated records, so `base + n` walks those two
straight into other spells.

Advancing is the ordinary rank-table walk (#813): every rung casts a shared **dispatcher** (`Spellweaving`,
duration 0), whose own record is a descending `CastNano` chain gated on
`MustHavePassiveAbilityRunning <the rung below>`.

**The backlash — ONE roll, partitioned six ways.** Each rung's `Detrimental Effect` record is a run of six
`CastNano` elements. The FIRST reads stat 520 `Rnd`, which draws the number; every later one compares stat
522 `LastRnd`, the value that draw left behind, against a band of it. So at most one backlash can land per
rung. Reading them as six independent chances would apply several at once.

| rung | own critical | own signature | Soul Wrack | Arcane Drought | Frailty | Ether Drain | escape |
|---:|---|---|---|---|---|---|---:|
| 1 | 0–5 | 6–17 | 18–25 | 26–37 | 38–56 | 57–75 | 24% |
| 2 | 0–5 | 6–18 | 19–27 | 28–40 | 41–60 | 61–80 | 19% |
| 3 | 0–5 | 6–19 | 20–29 | 30–43 | 44–64 | 65–85 | 14% |
| 4 | 0–5 | 6–20 | 21–31 | 32–46 | 47–68 | 69–90 | 9% |
| 5 | 0–5 | 6–21 | 22–33 | 34–49 | 50–72 | 73–95 | 4% |
| 6 | 0–5 | 6–22 | 23–35 | 36–52 | 53–76 | 77–99 | **0%** |

The bands are identical across all four classes; only the first two outcomes differ (Necromancer
Rebellious Spirit / Infestation, Demonologist Flesh to Dust / Demon, Tempest of Set Deadly Feedback /
Twisted Rune of Set, Priest of Mitra Shunned by Mitra / Flesheater). `Soul Wrack` tiers with the rung — a
stronger copy every two rungs. By the sixth component a backlash is certain, which is the escalating risk
the mechanic is built on.

⭐ **This is what the capture's "third trance buff" was.** NecroL80's activate carries three buffs, and the
third is Arcane Drought — which sits in band 26–37 of rung 1. It was hardcoded as a fixed trance buff, so
every weave opened with the same debuff and the other five outcomes were impossible. The trance opens with
**two** buffs (the `Spellweaving` trance and `Spellweaving (1)`); the third is a roll.

⚠️ **`Spellweave Gone Bad: Weak/Moderate/Strong/Maximum` (196612–196615) is NOT the backlash.** Those four
sit beside `TEST Spellweave: Fire 1` in a low id band and are referenced by **zero** of the 21,502 spell
records. Dead test content — the name is the only thing about them that fits.

⚠️ There is a **fifth** `Spellweaving (1)..(6)` band and it is SUPERSEDED: 3364577 + 3364671–3364675, on
Demonologist line 44000. No `StartSpellWeavingMode`, no damage ramp, no backlash; flat 12,000 ms durations
instead of the live 12/17/22/27/32 ladder; it chains FORWARD instead of through a dispatcher; and its top
rung modifies NECROMANCER spell lines from inside a Demonologist ladder. The old design, left in the data.

**Two more rules the trance record states**, neither of them wired yet: the trance buff casts
`Spellweaving Remover` (4079805) when `CombatMode != 39`, so leaving the weaving stance cancels the weave;
and every `Knockback` spell in the catalog casts that same remover, so **being knocked back ends your weave**
(rides #496).

## Which class gets which combo

The map (`ClassCombos`) has five sources. The second was added in #296 and the third, fourth and fifth in #581, each
because the ones before it refuse to guess:

1. **The combo's NAME prefix**, for the class-named ones (`Guardian_`, `Ranger_`, …) — `CaptureAbilityImporter`,
   plus captured action bars for direct `(class, combo)` evidence.
2. **The stacking line of the SPELL the combo drives.** A combo's `GameActionId` is a spell id, and a class-encoded
   `Spell.GuiStackingLine` (`ConanClass × 1000 + lineIndex`) names the class. This resolves the FEAT-TREE prefixes
   the name heuristic cannot — `Reaver_` → Barbarian, `Liberator_` → Conqueror, `Halberdier_` → Guardian —
   and it agrees with source 1 on **257 of 257** combos where both speak.
4. **The GATING FEAT**, which LEADS all of them — see below.

⚠️ **Only lineIndex ≥ 400, and this is the whole subtlety.** The ARCHETYPE-shared combos sit in a low block that
reads as class 20 and is not one:

| prefix | stacking lines |
|---|---|
| `Soldier_` | 20002–20005 |
| `Rogue_` | 20006–20007 |
| `Priest_` | 20008 |

Three different archetypes under the same leading `20`, so the number is a BLOCK, not a class — taking it at face
value files every Rogue and Priest archetype combo under Guardian. The split is exceptionless across all 17 name
prefixes in the catalog: every class-specific and feat-tree combo is at 401+ (Barbarian 18401–18409,
Guardian 20401–20406, Halberdier 20407–20410, Ranger 39401–39412, …) and every archetype-shared one is at ≤ 8.

### 3. The ARCHETYPE, for the shared block (#581)

An archetype-shared combo belongs to **all three classes of its archetype**, so `Soldier_` / `Rogue_` / `Priest_`
fan out where a class-named prefix names one. The membership is `ConanArchetypes`:

| archetype | classes |
|---|---|
| Soldier | Guardian, Dark Templar, Conqueror |
| Priest | Priest of Mitra, Tempest of Set, Bear Shaman |
| Rogue | Barbarian, Assassin, Ranger |
| Mage | Demonologist, Herald of Xotli, Necromancer |

The client ships the archetype **ids** (RDB 1000505 `RDBPlayerClassData_c`, tier 1) but no membership table — not
in the class registry, not in the race × class table (1000507), and not in the CHARACTERCREATOR XML (1010700),
which is appearance only. So the membership is taken from the game's published documentation
([aoc.fandom.com/wiki/Class](https://aoc.fandom.com/wiki/Class), which lists it twice), and it is **corroborated
by the corpus on one arm**: the captured Assassin's action bar carries `Rogue_15_dotdetaunt_Vicious_Strike_II_2h`
and `Rogue_19_damagedetaunt_Cunning_Strike_VI_2h` — two `Rogue_` combos on an Assassin, who is a Rogue. Nothing
in the corpus contradicts any of the twelve placements.

⚠️ **The same block was wrong on the SPELL side, and that is where it was doing damage.** `RdbSpellImporter`'s
class map read `line / 1000` with no guard at all, so `ClassSpells` gave **Guardian** all 22 of the Rogue and
Priest archetype spells — Cunning Strike I–VI, Vicious Strike I–II, Censuring Strike I–III — while no other class
held a single archetype spell. It now resolves the archetype **per stacking LINE**, from the combos that drive
that line's spells (per line, not per spell: a line's spells are one combo family's weapon variants and only some
have a catalogued combo). Lines 20000 and 20001 carry no combo at all, so they are left **unattributed** rather
than handed to Guardian.

⚠️ **`ClassSpells` membership is a DISPLAY grouping, not a spellbook** — the stacking line is what the client
groups icons by, so anything carrying a class's line is attributed to that class whether or not a player can ever
cast it. Two kinds of non-castable record do:

| band | how it gets in | proof it is not learned |
|---|---|---|
| **combos** (#298) | `Combos.GameActionId` IS a `Spells.Id`, and a combo's GameAction carries a stacking line | across the four captured characters' known-spell lists (246 entries) not one is a combo GameAction |
| **effects** (#847) | the DoT a spell APPLIES carries its **caster's** line | the captured L80 Necromancer knows all five ranks of `Flesh to Worms` (2/25/40/55/70) and **zero** ranks of the `Infestation` it inflicts |
| **what a combo CHAINS** (#852) | `SpellAreaEffects` kind `CastNano` is a spell→spell apply link, and a combo's GameAction inflicts its debuff through it — which then carries the caster's line | of the 249 spells the five imported characters know, **not one** is chained by a combo |

⭐ The third band is the melee classes' entire "spellbook": `Staggering Blow I-IV` (Barbarian), `Lethargy I-III`
(Assassin), `Bleeding Puncture I-IV` (Ranger), `Disable I-III` (Guardian). The rule takes **29 of the 68** those
five were offered — all four of the Barbarian's and all three of the Assassin's — and leaves the Conqueror with a
list that finally reads like a Conqueror's (`Rally`, `Battle Cry: Inspire the Ranks`, `Aura: Hoarfrost`,
`Guard of Dancing Steel`).

⛔ **The broad form — "chained by ANY spell" — is not safe, and was measured and rejected**: it takes six of the
level-80 Necromancer's own 109. Only a COMBO chainer is a statement about its target.

⚠️ It leaves the Barbarian and the Assassin with **zero** level-granted spells, and that is the shape of the class
rather than a gap: they hold 103 and 75 **combos**, which travel their own path.
`EveryClass_ReachesLevel80WithSomethingToCast` asserts on the whole kit for exactly that reason, and
`AClassTheFeatMapCoversStillHasASpellbookAt80` holds the line for the classes that really are casters.

An effect's ranks pair with the **caster's** ranks rather than with a level ladder of their own, so all five
`Infestation` ranks sit at `Level 2` and a single 1 → 2 ding taught the whole ladder. A flat rank ladder is the
tell: only 7 of 172 ranked lines have every rank at one level.

⛔ **"Exclude anything named as some spell's `EffectSpell`" is the wrong rule.** `Flesh to Worms (Rank 1)` is
itself named as the effect of `Plague Swarm`, so the naive predicate deletes the very spell the level-2 ding
exists to teach. The referrer must itself be a real **book** spell — `Plague Swarm` is `Level 0` and grants
nothing. Both exclusions apply to all three grant paths (the ding, the relog re-grant, the default bar seed),
never to the captured `IsStarter` kit, which is retail's own list.

#### ⭐ The third band, and the one that closes most of it: a spell no FEAT teaches

The client grants a spell rank through a **feat** — a `<Class> Class Feat NNN` for rank 1 and a
`Spell Unlocking <lo>-<hi>` for ranks 2 and up, whose name carries the level band it covers (`FeatSpellGrants`).
Anything in a class's stacking line that no feat teaches was never on a level path, and the client would refuse
the cast anyway: its criteria evaluator gates on carrying the teaching feat (`YouCanNotUseYet`).

⭐ **And only the AUTOMATIC feats count** — `Feat.AutomaticUnlockCategory` (7063). Measured over the 946 feats
that grant a spell the split is exact: the 271 in that category resolve to 24 names and no others
(`Spell Unlocking <lo>-<hi>`, `<Class> Class Feat NNN`, `Pet Mastery`), while the 675 in category 8072 carry 238
names, every one of the form `Spell - <the spell's own name>` — a per-spell **purchase** in the feat tree.

| | offered before | any feat | AUTOMATIC feat | actually known | EXTRA |
|---|---:|---:|---:|---:|---:|
| Necromancer | 230 | 111 | **97** | 109 | **0** |
| Demonologist | 201 | 109 | **96** | 127 | **0** |
| Herald of Xotli (L24) | — | 10 | **5** | 8 | **0** |

The automatic gate reproduces the captured spellbooks with **zero** false positives on every character in the
corpus. Gating on "any feat" instead leaves 20 extra on the Necromancer, and they are exactly 8072 rows —
`Ritual of the Lich`, `Despoil the Soul`, `One with Death` — tree spells that player did not spec into. The three
the same player *did* spec into are 8072 too, so what separates the two populations is a point spend, not a
property of the spell. `FeatService` hands a tree spell over when its feat is **trained**
(`GetSpellsTaughtByFeats`), which is the mechanism retail uses and the only path those spells now arrive by.
`--report spell-grant-check` is the standing measurement.

⚠️ The category is used rather than the 24 names because it is a client field and because it is strictly better:
`Pet Mastery` carries it, matches no name pattern, and is what teaches the captured Necromancer its `Ice Shackle`.
⛔ It does **not** establish what 7063 means in general — 16,051 feats carry it, most of them armour and equip
feats. The claim is scoped to the feats that grant a spell.

⚠️ **Applied PER CLASS** (`DatabaseRepository.FeatMapCovers`). The feat map grants **Barbarian, Guardian,
Conqueror, Assassin and Ranger** nothing at all — 227 of their 259 level-gated entries are combos, which travel
the combo book — so for them its silence is missing data, not a statement, and gating on it would ship an empty
spellbook with no capture to notice. The population is bimodal with nothing between: those five sit at **exactly
0** feat-taught rows and the next class up at **29**, so "does the map grant this class anything" is a real
separation and not a threshold chosen to fit. Held by `AClassTheFeatMapCoversStillHasASpellbookAt80`, which
asserts the converse — a class the map *does* speak for must still have a book.

### The membership map's own blind spot, and the third source that closes it

`GuiStackingLine` is only present on **3,158 of 21,502** spell records, so the class map is structurally blind to
the rest — including the whole `Undead Minion:` / `Deathless Acolyte:` / `Dread Archmage:` pet ladder
(35/35/40/55/65/65/70/80), every rank of which a `Spell Unlocking` feat teaches and which therefore belongs on
the level path. Of the 18 spells the captured level-80 Necromancer knew and the map had never heard of, **16
carry `GuiStackingLine = 0`**.

The third source is the captures themselves: every `CharacterSpell` row is a statement that a character of that
class held that spell. The `class-spells-known` importer module derives membership from it — 211 capture-proven
`(class, spell)` pairs, 15 of them new — and it cannot produce a false positive, because a character really did
have the spell.

⚠️ **LEVELED spells only.** A book also holds what the character OWNS or EARNED, and those carry `Level 0`;
claiming class membership for them is a category error that
`NoClassSpellRow_ContradictsItsSpellsStackingLine` catches — the captured Necromancer's `Blue Snow Mammoth`
sits on line 44224, i.e. filed under Demonologist, because **a mount belongs to no class** and its line is not a
class statement. The check is byte-identical with 15 rows as with 45, so the `Level 0` half was doing nothing.

⚠️ What is left is **not** the level path's business at all. Every remaining miss carries `Level 0`: mounts,
pets and companions, `Dance:` / `Social Ability:` emotes, `Path:` travel unlocks and veteran rewards — earned or
bought, never leveled into, and replayed on login from the character's own rows (`GrantCharacterSpells`) rather
than re-derived from the class.

The five uncovered classes are handled by the combo-CHAIN exclusion above rather than by the feat gate, which
takes 29 of the 68 they were offered. What survives it is the Conqueror's `Colossus of War (1..20)` — twenty
stacking increments of a `Colossus of War` that carries no class row, so no exclusion reaches them — plus four
strays: Gitea #854.

### 4. The GATING FEAT (#581) — the source that LEADS

`Combos.FeatId` is not a `Feats.Id`; it is the **wire** feat id, so it resolves through
`FeatIdMaps.WireFeatId → TemplateId → Feats.Id`. **482 of the 744** catalogued combos carry one — and **none of
the 97 `combo_npc_` / `monster_` / `generic_` NPC combos does**, which makes "carries a gating feat" a mechanical
discriminator for *this is a player combo* in its own right.

The feat's own NAME states the class, in three shapes:

| feat name | reads as | example |
|---|---|---|
| `<Class> Class Feat NNN` | that one class | `Guardian Class Feat 041` → Guardian |
| `<Archetype>: Class Specific N` | the archetype's three | `Rogue: Class Specific 1` → Barbarian/Assassin/Ranger |
| `<Class>` | that one class | `Ranger` → Ranger |

⭐ **Validated before it was trusted: where the feat and the name prefix both speak, 134 agree and ZERO conflict.**
That is what earns it precedence — it is narrower than the prefix on three combos and wider on none, so it can
only ever correct in the safe direction. `FeatGating` (`Faolan.Importer/Captures/FeatGating.cs`) is the reader.

What it adds beyond the prefixes: `SneakAttack_*` (7 to the Rogue archetype, plus `_bow`/`_crossbow` to Ranger via
the bare `Ranger` feat) and `Halberdier_50_…_Bloody_Vengeance_polearm` → Guardian.

What it **corrects**: `Priest_05/10/15_damagedetaunt_Censuring_Strike_*_2hb` sits on the Priest archetype's
stacking line 20008, so source 3 fanned it out to all three priests — but its gating feat is
`Bear Shaman Class Feat 001`/`011`, the *same* feat that unlocks `BearShaman_05_dot_Internal_Bleed_I` and its
siblings. A combo is granted by the feat that teaches it, so the three are Bear Shaman's alone.
⚠️ **Assumption:** this reads the missing Priest of Mitra / Tempest of Set variants as absent from the catalog
rather than as classes sharing the 2HB one. `Soldier_`/`Rogue_` each ship three weapon variants where `Priest_`
ships only this one, and the two-handed blunt is the Bear Shaman's weapon — so the shape of the gap is a missing
variant.

**It also settles the LEVEL for some combos.** `Combos.Level <= 1` is the catalog's sentinel for *not stated*, and
where every LEVELLED combo a feat gates carries one level, that level is the feat's and is lent to the sentinel
ones. `Rogue: Class Specific N` is exactly that shape (N → level 2N+3, measured: 1→5, 2→7, … 8→19, and the Soldier
arm agrees independently), which puts the seven `SneakAttack_*` at **level 5** rather than at the sentinel.
⚠️ Most feats gate a RANGE (`Ranger Class Feat 001` covers 5/7/10) and lend nothing.

### 5. The FEAT TREE, for the members its own siblings have already answered for (#581)

A combo's tree is the leading segment of its name — `Halberdier_20a_multihit_Flashing_Arc_II` is in the
`Halberdier` tree — and **a tree belongs to one class**. Three of them have said so themselves, through their own
members' gating feats:

| tree | attributed by their feats | class |
|---|---|---|
| `Reaver_` | 29 of 30 | Barbarian |
| `Halberdier_` | 26 of 30 | Guardian |
| `Liberator_` | 25 of 27 | Conqueror |

Unanimous in all three, so the answer applies to the stragglers — and the stragglers are exactly the members whose
gating feat names a **weapon line** (`Pole Arm Combos 20-40 1`) or an **ability** (`Combo - Skewer`,
`Combo - Whirlwind`) rather than a class. That is a gap in what the feat's NAME happens to say, not evidence that
the combo belongs to another class.

⚠️ Three guards, each removing a way this could attribute wrongly:

- **unanimity** — a tree whose attributed members name more than one class lends nothing, which is what keeps the
  ARCHETYPE trees (`Soldier_`, `Rogue_` → 3 classes each) and the RACIAL dances (`Cimmerian_` → all 12) out of it;
- **an evidence floor** of three attributed members, so one stray row cannot define a tree;
- **NPC/monster names excluded outright** — `combo_npc_guardian_polearm_1_sweep` splits to `combo`, which would
  otherwise be the same "tree" as the player-facing `Combo_Charge_*` family.

Additive, never subtractive, like the prefix rule: it cannot tell "no opinion" from "no class".

Gated by `Faolan.Tests/ClassComboMembershipTests.cs`, which pins all of it: the stacking-line rule never
contradicts source 1, every shipped row agrees with its combo's gating feat, each archetype combo is mapped to
exactly its archetype's three classes, the archetype spells go to those three or to nobody, and each of the three
trees above is complete and single-class while the archetype/racial ones keep all of theirs.

Which classes the remaining ambiguous prefixes belong to — open work tracked in Gitea #581.


## Data model (DONE)

- **`Combos`** catalog — RDB 1010206 (plaintext XML), **744 rows = all 744 entries of the type**. `Name` is an engine
  name `{Class}_{level}_{tags}_{Display}_{Rank}`, `Level` = unlock level, `Actions` = the required input (melee:
  `UR,UP`; weave/reactive: markers like `MagicCombo`/`Magic_Reactive`), `GameActionId`, `ParentId` (= the
  weapon-VARIANT group root, NOT the input chain), `Criteria` (weapon/state).
  ⚠️ A combo with **no `<action>` children is written SELF-CLOSING** (`<combo … />`) — 151 of the 744: the bow attacks
  (`Freefireattack`, `Sniperattack`), `firebreath`, `PowerAttack`, `Bladestorm`, the racial dance emotes. The carve
  used to require a `</combo>` and skipped them silently, which is what made #313 read the
  `0x00416541..0x00416549` block as "cataloged nowhere in faolan.db" and hunt for a TEA-encrypted RDB type that was
  never involved. `RdbXml.Carve` handles both forms, for every RDB XML reader (`RdbXmlCarveTests`).
- **`ClassCombo`** access map — `(Class, ComboId, Level)`, from **four** sources, **686 rows**:
  - the **GATING FEAT** (`FeatGating`), which runs FIRST and wins: 142 combos, and the only source that can be
    narrower than the name. See *4. The GATING FEAT* above.
  - the `Combos` name prefix (`CaptureAbilityImporter.DeriveClassCombosAsync`): the 8 literal class-name prefixes =
    the 8 archetypes that have combos. Validated by Hakotep's HoX `0x2076` combo book. 292 rows; casters empty.
  - the **ARCHETYPE** prefixes in the same pass (#581): `Soldier_` / `Rogue_` / `Priest_` name an archetype rather
    than a class, so each fans out to that archetype's three (`ConanArchetypes`).
  - the **universal kit** (`DeriveUniversalCombosFromCreatesAsync`): the 4 playable races' dance emotes
    (`Cimmerian_`/`Khitan_`/`Stygian_`/`Aquilonian_Dance_combo1..3`), 12 × 12 classes = 144 rows at level 0.
    ⚠️ **Racially NAMED, universally GRANTED** — across the corpus **19 characters spanning every class receive the
    same 12**, i.e. all four races' dances, so the grant follows neither race nor class. The prefix derivation
    structurally cannot express that (it keys on CLASS names), which is why `Combos` held all 12 while `ClassCombos`
    held none and the emotes reached nobody (#416). Derived as the **intersection** across create dumps, not the
    union the abilities twin uses: a create burst also carries the class's own combos (the Ranger's 14, the HoX's
    21), and a union would hand every class the Ranger's. Guarded by `UniversalComboKitTests`.
- **Grant** — `GetClassCombos` returns **every rank the character qualifies for** (Hell Strikes I *and* II), and
  `GrantCharacterCombos` + the level-up dings emit them all. Showing only the highest rank of a line is a
  **client-side display filter**, not something the server does — an earlier "rank-supersede per family" collapse
  here was wrong about that and has been removed.
- **`ComboActionParser`** (`Faolan/GameServer/Combat/ComboActionParser.cs`) — normalises the catalog's `Actions`
  token spellings into the `ComboInput` enum (4 quadrants + Up/Head/Thrust); returns false for the non-directional
  markers (spellweaving / reactive). The normalisation is the point: the same key appears as `UR`,
  `CombatUpperRight`, `Up Right`, `UpRight` and `1_Combatupperright`. Gated over the whole shipped catalog by
  `ComboActionParserTests` — every distinct `Actions` value must parse, carry a known marker, or be blank, with
  **zero** unknown spellings, so a new catalog spelling fails loudly instead of being dropped from a sequence.
  ⚠️ Nothing calls the parser yet: its only consumer was the combo service dropped as dead in #353.
- **The marked combos: the direction is a SELECTOR, not a step (#455, DECODED).** The non-directional markers are a
  **prefix** — every one reads `<marker>,<direction>` (`MagicCombo,Combatlowerleft`). The catalog settles what that
  direction means: the marked rows are a complete **4 families × 5 directions grid, each cell present exactly once**,
  and the three *reactive* families share **one `GameActionId` per direction**:

  | direction | `*_Reactive` effect | `MagicCombo` effect |
  |---|---|---|
  | `CombatHead` | 231964 Debilitating Cleave | 700777 Exhaustion |
  | `Combatupperleft` | 227061 Rage | 700755 Mana Leech |
  | `Combatupperright` | 227064 Furious Blow | 700751 Discourage |
  | `Combatlowerleft` | 227060 Clumsy | 700749 Boiling Mana |
  | `Combatlowerright` | 227063 Weaken | 700791 Mind Purge |

  So the **family says what TRIGGERS the proc** (a completed combo / a spell / a melee hit) and the **direction says
  WHICH proc** — each row's own `Name` is that spell's name (`Clumsy_combo` / `Clumsy_magic` / `Clumsy`), which makes
  the mapping self-checking. A key SEQUENCE could not come out as a perfect grid with shared ids down the columns
  [V3: ComboActionParserTests.MarkedCombos_AreAFourByFiveGrid_TheDirectionSelectingTheProc]. `ComboTrigger` +
  `ComboActionParser.TryParseTriggered` keep the direction the sequence parser discards; `TryParse` still refuses
  these, since they are not a key sequence.
  `action` (69 rows) is the fifth marker and the only one with **no** direction after it — and it is the **NPC combo
  set**. All 69 rows are named `combo_npc_*` (`combo_npc_generic_polearm`, `combo_npc_guardian_polearm_1_sweep`,
  `combo_npc_barbarian_2h_1_butcher`, …), and the corpus confirms it from the other side: those ids appear **only
  s2c, with an NPC's actor handle, in 0 of 153 client requests** — the AI drives them, so there is no key sequence
  because there is no keyboard [V3: ComboWireTests.NpcComboRows_AreTheActionMarkerSet]. They carry `combo_attack_num`
  0 or the unstepped sentinel `0xFFFFFFFF`, never a real step. ⚠️ This is NOT the "input the catalog does not model"
  bucket it used to be read as — that is the **160 blank-`Actions` rows** (`Freefireattack`, `firebreath`,
  `PowerAttack`, `Bladestorm`, the mounted charges, the racial dance emotes), which carry no marker at all and
  remain unexplained.

## Wire decode

**Melee** — DECODED. The combo is **stated on the wire**, not inferred (dumps: `AssassinL23_rogue-assassin_login_combos`
[Assassin, incl. a few DELIBERATELY FAILED combos], `Hoxxling_mage-heraldofxotli_combos`, `Hakotep_hox_login_gameplay0`,
and 23 more — 840 swings in all). Re-derive the whole census with
`dotnet run --project Faolan.Importer -- --report combo-swings`.

- A combo is a **sequence of `Combat:Melee` (BehaviorControlCenter, `BehaviorId` 0x0F)** actions. **No separate
  activation packet** precedes the swings — the swings ARE the input, not an "activate combo X then confirm" flow.
- The record's args are a **`CommandMessage`** (the client's own descriptor — see
  `docs/re/behaviorcontrolcenter-grammar.md`), and three of its fields are the combo:

  | field | name | meaning |
  |---|---|---|
  | 7 | `combo_id` | the `Combos` catalog id, **verbatim** |
  | 8 | `combo_attack_num` | the **0-based step**, absent on the opener (protobuf omits a zero) |
  | 9 | `combo_attack_hits` | the hit count — never set anywhere in the corpus |

  Across 840 captured swings in 26 captures, all 60 distinct `combo_id` values resolve to a shipped catalog row,
  with **zero** unknowns, and `combo_attack_num` never once runs past the combo's own step count
  [V3: ComboWireTests.CapturedComboSwings_AllResolveToTheCatalog,
  ComboWireTests.ComboAttackNum_StaysInsideTheCatalogStepSequence]. So the server does not have to match key
  sequences at all — the client says which combo and which step, every swing.
- **Phases** (the record's `Seq`): c2s `0x03` request → s2c `0x04` ack, and c2s `0x02` → s2c `0x07` is the ABORT
  pair (the capture's deliberately failed combos). `commandqueue_client_id` (field 2) is the client's own monotonic
  command counter and `commandqueue_server_id` (field 3) the server's, assigned in the ack — they drift apart
  exactly where the client abandoned a command.
- `recharge_time` (11) and `activation_time` (12) are **centiseconds**, and both are per (combo, step, animation):
  a non-final step reports 56–60 cs and the finisher its own longer animation (Grim Corruption II 60 + 115, Vicious
  Strike II 60 + 60 + 170). Summing `activation_time` over the steps is the server's only source for what the
  client calls `BCTCombo::GetComboAnimationLength`.
- **`bcc_action_id` (field 1) is the swing ANIMATION, and per class+weapon it *is* the direction** — one id per
  direction within a family, which is what the older raw-byte read was reaching for when it called the varint's
  first byte an "animByte":

  | family | UL | UR | UP / Head |
  |---|---|---|---|
  | Assassin / Rogue (dagger) | 49732 | 49730 | 49728 |
  | Herald / Conqueror / Soldier (2h) | 41906 | 41907 | 41908 |
  | Slave / Bear Shaman (2hb) | 48878 | 48877 | 48876 (Head) |

  The `96xxx` / `200xxx` ids are the session's first swing of a family — a distinct draw-and-strike animation
  [V3: ComboWireTests.BccActionId_IsTheDirection_PerClassAndWeapon]. Nothing needs this map, because the combo and
  step are stated outright; it is recorded because it is what the old read was missing.
- **Every step deals damage**, not just the finisher: both steps of the captured `Slave_1_AOE_2hb_Sweep_2` carry an
  81-byte hit-result block, and the AoE forms carry 130/138/179-byte ones (a second and third victim at 49 bytes
  each). Measured against the same attacker's plain swings the opener lands at ~1.4–1.7× and the finisher far
  above it (Slave_1 Sweep 2.2–4.8×; `Soldier_19_damagetaunt_Brutal_Enraging_Strike` 3.2× / 2.7× / **16.8×**). The
  spread between combos is per-combo, which is what a per-combo payload produces and a uniform step multiplier
  could not — see the implementation below.

⚠️ **The superseded read.** This section used to describe the swing body as
`[08][animByte][2B marker][u32 counter][u32 cid = 0x300038XX][tail]` and conclude that the `cid → combo` and
`animByte → direction` maps were computed client-side and needed Ghidra. That was the protobuf misparsed as raw
bytes: `0x300038XX` is `[30 00]` (`execution_status` = 0) followed by the `[38 …]` `combo_id` tag, and the
"counter" was `[10 …][28 …]` (`commandqueue_client_id`, `flags`). The `cid` low-byte "coincidence" was the combo
id's own low byte, which is why it matched for the HoX. No client RE was needed for any of it.

**Spellweaving** — DECODED (`Captures/NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving`, a necro
that ate 3 potions/food then wove Arcane Renewal + Arcane Surge + Death God then stopped):
- **Activate / deactivate = a TOGGLE**: c2s method `0x205a`, body `[u32 0x000000fc]` (same value both times, like
  the mount toggle) — enter the trance (immobilise) on the first, fire + leave on the second.
- **Add-component**: c2s `0x2000` carrier, IIR sub `0x54C02089` = **`CombatReactiveIIR_c`** (reader `FUN_0082c720`,
  docs/re/iir-readers-combat-world.md) — the same family as the catalog's `*_Reactive`/`MagicCombo` Action markers.
  Body `[u32 0][u8 0x03][u32 componentIndex]` (+ a `02`-tagged twin ack). `componentIndex` = 0/1/3 for the three
  spells → an index into the caster's weave elements, NOT the Spell catalog id (the spell id never appears on the
  wire — matching "extra stats/effect per input"). The adds are ~10-15s apart (the paced trance cadence).
- **Activate response** (s2c): 3 trance buffs (`ApplySpell 0x6B87E316`, ids 0x37F29800/0x37E5A800/0x3969D000) + the
  reactive element LIST (`0x54C02089`, the addable elements) + a trance-pose BCC animation (`0x642CD3D6`).
- **Energy bar** (the yellow "sprint" bar, a SEPARATE universal pool from the class-resource stamina stat) drains
  during the weave: **`DispatchUpdateSprintbar 0x200E`** (`[u32 energy][u8 01]`) is the packet that moves the bar
  (full **9000** for the necromancer and demonologist, **14000** for the melee classes - retail's idle "bar is full"
  pushes carry exactly those, and they do not scale with level), with a parallel `0xe9f24184` "Spellweaving Cost"
  gauge (`0x37E68C`). When energy hits 0:
  the `Exhausted` debuff (0xB90) + the `0x37E690` gauge draining HEALTH (the over-weave DoT).

### The energy pool is a CLIENT model the server feeds through four stats

The yellow bar is not drawn from the `0x200E` packet alone — it is a client-side simulation, which is why the
BAR's own drain during a sprint barely reaches the wire (`0x200E` is 509 frames over just 13 captures, against the
weave's steady stream). ⚠️ That is a statement about the bar packet only: the sprint itself is *not* silent — see
the sprint family below, which retail sends 4,835 times. The client
registers every stat id against a name in a static metadata table (`tools/ghidra/DumpStatNameTable.java`), and four
of them are the whole model. `FUN_0112ad70` listens on exactly these ids and caches them side by side:

| stat | client's name | role | on the wire? |
|---|---|---|---|
| `1000028` (`0xF425C`) | `Energy` | the pool itself | **yes** — 1,650 records on `StatUpdateIIR` (`0x96B8DC59`), per entity |
| `1000029` (`0xF425D`) | `MaxEnergy` | the bar's denominator | never observed |
| `1000031` (`0xF425F`) | `EnergySprintDrain` | drain rate while sprinting | never observed |
| `1351` (`0x547`) | `NaturalEnergyRegeneration` | regen rate | never observed |

The names are the client's own, not ours: the same table's low half round-trips against ids recovered from entirely
different directions (`0x324 Strength`, `0x42F OffhandChance`, and `0x547`, which LDB category 615 also names
"Natural Energy Regeneration").

**The bar renders `Energy / MaxEnergy`.** `SprintBarView::Update` (`FUN_0155b390`) re-reads `MaxEnergy` on every
update, and the renderer (`FUN_0155b570`) computes `current / max * 100` **only `if (max > 0)`** — so a `MaxEnergy`
of zero draws the bar at 0% whatever current is pushed. The view's init (`FUN_0155b110`) seeds *both* current and max
from `MaxEnergy` and then takes its current from the energy model's cached `Energy`, so the `0x200E` stream is a
correction on top of the client's own simulation rather than the sole source.

⚠️ `MaxEnergy` and the two rate stats appear **zero** times in any stat block across the 120-capture corpus — the only
`0x000F425D` bytes anywhere sit inside the retail server's leaked scaffold region on `0xFA015807` spawns (#603), which
is not a value it wrote. So the client sources the ceiling and the rates itself; do not invent a push for them.

`Energy`'s own-handle series independently re-confirms `EnergyMaxForClass`: it tops out at **exactly** 9000 for
casters and **exactly** 14000 for the rest, and retail's cadence is a push every ~3–7s while the pool is moving,
going silent once it is back at the max.

### The sprint family: four messages, and the server owns the state (#867)

Sprinting has its own method pair, separate from `PerformSpecialAction` and from the bar. `--report sprint` is the
oracle for everything here (134 captures, 14,164 frames).

| dir | method | body | corpus |
|---|---|---|---|
| c2s | `SetSprintInputState(bool sprintInput, bool clientCheck)` | **two** bytes | 9,329 frames / 90 captures — the third-busiest client message there is |
| s2c | `DispatchSetIsSprinting(isSprinting)` | one byte | 2,624 / 65 |
| s2c | `DispatchSyncCanSprint(canSprint)` | one byte | 476 / 116 |
| s2c | `DispatchSyncRegenDelayed(regenDelayed)` | one byte | 1,226 / 65 |
| s2c | `DispatchUpdateSprintbar(energyValue, forceUpdate)` | `[u32][u8]` | 509 / 13 |

⚠️ **The c2s body is two bools, not one `u16`.** All four combinations occur — `0/1` ×3,714, `0/0` ×2,954, `1/1`
×1,958, `1/0` ×703 — and the names are the client's own signature. Reading it as a single `u16` compared to `1`
selects `00 01`, which is the player **not** sprinting: the commonest frame in the whole family. `clientCheck` is
the client's local verdict on whether it believes it may sprint; the server is not obliged to agree.

**Retail answers a CHANGE of the request, and it does not merely echo.** 2,959 changes of `sprintInput` drew 2,624
`DispatchSetIsSprinting` frames, and 14.5% of those answers carry a *different* value from the input before them —
the server refusing. So `isSprinting` is server-owned state, not an ack.

**Each flag is addressed through the RECEIVER ENDPOINT, not the body.** A one-byte body names no entity; the
character a flag is about is the receiver endpoint's instance, and in all 65 captures carrying `isSprinting` that is
one instance per capture — the session's own character. These are not broadcasts about other players. The sender is
`GameCharAgentProject` at the session's small instance, the same pair `0x200E` uses.

`regenDelayed` is **`00` in all 1,226 captured frames** — every send clears the delay, most of them just after a
sprint ends (514 follow an `isSprinting = 0`, median 820 ms later). The corpus proves the transport and the clear;
what SETS it is uncaptured. `[V0: what sets regenDelayed — no capture carries a 1]`

**`Exhausted` is server-applied on the sprint path too**, not only the weave. In `Hoxxling_..._gameplay2` the `Energy`
stat reaches exactly 0 at 20:53:41.247 and in the same millisecond retail sends `ApplySpell 0x6B87E316` carrying spell
id `000B9000` (base `0xB90`, form 0 = apply), applyTo == applyFrom == the player; 3.4s later, on the first push that
lifts energy back above zero (1019), it sends `000B9001` — the form-1 END twin with an empty payload. The client has
its own exhausted STATE keyed on the same stat, but that is separate from the icon.
- **Fire / cancel**: on the 2nd toggle, trance-buff end-twins + a trance-EXIT BCC. Per add: the component's own
  spell as an immediate self-buff + add-gesture BCC; the Spellweaving(1) buff re-applies at an incremented id each
  picker reappear (stacks ~x2).

⚠️ **The `f98e10b3` burst next to the fire is NOT the weave firing** (#685). Those four `StatIIR` records were read
as opaque "fire stages" `0x398 / 0x39B / 0x39A / 0x39D` carrying a constant `0x7BD4`. Every part of that is a
misread: the ids are the client's own `AttackRating` / `MagicRating` / `DefenseRating` / `MagicalResist`, and
31,700 is that **level-80 necromancer's own rating**. Swept across the corpus the four appear in **2,100 records
over 31 captures**, and **20 of those captures contain no spellweaving at all** — including a Herald of Xotli,
which cannot weave. They are the level-scaled rating quad ([`combat.md`](combat.md)), and the weave capture is
simply where one landed. The emit is gone, and a weave pushes nothing here — the search for a fire payload it
prompted ended in there not being one (see the ruleset above).
- `0x2085` (~2s heartbeat) and food/potion `UseItem` (BCC `BehaviorId` 0x0e) are NOT part of the weave.

## Implementation (spellweaving)

All four weaving classes have a profile; the wire is byte-exact against the one capture we have (golden tests
`SpellweaveCodecTests` + `BehaviorAnimationsTests`). Code map:

- `SpellweaveService` (`Faolan/GameServer/Combat/SpellweaveService.cs`) — the weave FSM + the trance-meter /
  energy loop. Session state on `NetworkClientSession`: `IsSpellweaving`, `WeaveComponents`, `SpellweaveBuffStack`,
  `Energy`/`EnergyMax`/`IsSprinting`/`EnergyLoopActive`. It reaches the wire through one narrow port,
  `ISpellweaveOutbound`: `ApplyBuff` (the trance buff and each component's own buff, via the shared
  `ApplySpell` path since #274), `RemoveBuff` (rank-1 end-twins, relayed to viewers so the pose ends on their
  screens too), `SendFeedback`, `SendSprintBar` (`0x200E`), `EnsureEnergyLoop`, and `IsMounted` — the trance roots
  the caster and plays a kneeling pose, so opening one from the saddle is refused with an on-screen line rather
  than pinning a rider mid-gallop. The port is `SpellweaveOutbound`
  (`Faolan/GameServer/Combat/SpellweaveOutbound.cs`), one forward per member.
- `CombatReactiveCodec` (`Faolan.Core.Network/Codecs/Combat/CombatReactiveCodec.cs`) — reactive-list, stat-gauge
  and buff-data builders.
- `BehaviorAnimations` (`Faolan.Core.Network/Codecs/Combat/BehaviorAnimations.cs`) — the trance /
  component / EXIT BCC animations (BCC `0x642CD3D6` is the generic behaviour channel, shared with combat).
- `SpellweaveCatalog` (`Faolan.Core.Network/Constants/SpellweaveCatalog.cs`) — the four `SpellweaveProfile`
  rows (elements, stack ladder, trance buff, damage step, the class's own two backlash outcomes), the shared
  backlash partition (`RollBacklash`) and `EnergyMaxForClass`. Its `FireStages` / `CapturedFireRating` are
  **gone** — see the rating-quad note above.
- `EnergyService` (`Faolan/GameServer/Combat/EnergyService.cs`) — `SendSprintBar` (`0x200E`) + the persistent
  energy loop (`RunEnergyMirror`: drain-on-sprint / regen-when-idle / step-aside-while-weaving; the sprint drain
  itself is client-side, the server mirrors it). The loop also pushes `CharacterStatId.Energy` on the retail
  cadence and owns the sprint-path `Exhausted` apply/remove — see the four-stat table above. Every value it
  touches is on the session, which is why it is a plain collaborator rather than listener state.
- `GameServerListener.Inbound.cs` — the `CombatReactiveIIR` add case + the `PerformSpecialAction` (`0x205A`) handler.

## Implementation (melee combos)

The runtime is `ComboService` (`Faolan/GameServer/Combat/ComboService.cs`), composed on the listener beside
`CombatService`. It reads the combo fields off each `Combat:Melee` request and tracks the running attempt on the
session (`ActiveComboId` / `ActiveComboStep` / `ActiveComboCentiseconds`):

- A swing with no `combo_id` **ends** the attempt — a plain hit breaks the chain, so the next combo swing opens a
  new one rather than resuming at a stale step.
- A step that does not continue the running attempt **restarts** it. Retail does this itself (the assassin capture
  re-sends step 0 twice inside one Cunning Strike), and the client is authoritative on which combo is running, so
  this is bookkeeping rather than a rejection. A `combo_id` outside the catalog resolves as a plain swing.
- `0xFFFFFFFF` is the **unstepped sentinel**, collapsed to a single-step combo — read as a signed step it would be
  −1 and index off the front of the sequence.

**The finisher's payload is a SPELL.** `Combo.GameActionId` resolves into the `Spells` catalog for **726 of the
744** shipped combos, under the combo's own display name (`Assassin_20b_…_Grim_Corruption_II` → 149094 *Grim
Corruption II*) [V3: ComboWireTests.ComboGameActionIds_ResolveIntoTheSpellCatalog]. So a combo's damage, its DoT
and its debuff are that spell's, and `ComboFinisher.Deliver` runs the same two ops a cast does
(`ApplySpell` + `CombatService.ResolveOffensiveCast`) instead of a bespoke combo-damage model — which is also what
the per-combo spread in the measured numbers above says it should be. The steps themselves need nothing special:
they are ordinary damaging swings already.

**Stamina** is spent on the finisher by the client's own formula (`FUN_007d7ba0`):
`MaxStamina(0x1F9) × (Combo.Stamina / 100) × animationSeconds ÷ ClassComboScaling.StaminaDivisor`, with the
animation length summed from the steps' `activation_time`. The client gates *starting* a combo on that cost being
affordable (`FUN_007d5930`), so the two ends agree rather than letting a player chain combos for free.

⚠️ The **DPS half of the same table is a tooltip number**, not the damage formula — see `ClassComboScaling` and
`docs/rdb/client-data-tables.md`. Nothing here scales damage by it.

**Reuse time.** `Combo.Cooldown` (seconds; 292 of the 744 rows carry one, from 5 s up to 300 s) is enforced per
session in `NetworkClientSession.ComboReadyAt`, keyed by the combo id. It is armed when the combo **goes off** —
on the finisher, so a sequence the player breaks half-way costs nothing — and it gates the **opener** only: an
attempt already under way runs to its finisher rather than being cut off mid-sequence.

A refused opener resolves as an ordinary white swing and **nothing else is sent**. That is the same refusal shape
the spell recast gate uses, for the same reason: retail's client will not *send* a combo whose own recast is still
running (its timer greys the icon), so retail's server has no captured answer to one. There is no on-screen
refusal line to send — every `FormatFeedback` template resolves a client-validated content hash (Gitea #664) — and
a chat line would be text retail never sends here, so the refusal is logged instead.

The key is the combo **id**, not a line: unlike a spell, whose ranks share one `SetSpellLineRecastTime` group, a
combo's ranks replace one another in the book (you hold *Vicious Strike II* **or** *III*, never both), so no two
ranks are ever held at once to need a shared timer. `Combo.ParentId` chains the tree if that turns out to be wrong.

Gated by `ComboServiceTests` (the runtime) and `ComboWireTests` (the wire claims, against the captured swings in
the `combo-swings.bin` fixture).

### ⭐ A combo's NAME states what it does

The client's combo catalog names a player combo `<Class>_<level>[_<expansion>]_<category>_<Name>`, and the
category token is a small closed vocabulary across the 675 player combos:

| token | combos | | token | combos |
| --- | ---: | --- | --- | ---: |
| `knockback` | **30** | | `snare` | 13 |
| `multihit` | 41 | | `manaburn` | 7 |
| `dot` | 26 | | `armorbreak` | 7 |
| `damagedetaunt` | 26 | | `stun` | 6 |
| `damagetaunt` | 18 | | the taunt/detaunt and debuff families | … |

**It is the only place the effect family is stated.** A combo's own spell says nothing about it: every stagger
combo resolves to a `Stagger I..V` / `Staggering Blow I..IV` record with a **zero duration and no stat effects**.
So `Halberdier_15_knockback_Stagger_I` knocking back is a fact recorded in the name and nowhere else.

⚠️ **Match the token, not a slot.** The Khitai combos carry an expansion token that pushes the category one
position along (`Barbarian_80_khitai_knockback_Clobber_V`), and only 422 of the 675 player names match the plain
four-token shape at all — the weapon specials (`2h_Sword_Wheel_of_Death`, `Oar_Special`) are a different grammar.
Reading `parts[2]` finds **25** knockbacks instead of 30, and the five it drops are the **rank-V top ability** of
five of the six ladders. That is a measurement I got wrong before the gate caught it.
[V3: ComboEffectTests.TheShippedCatalog_CarriesTheKnockbackCombos]

**Wired today: `knockback`.** The finisher of a knockback combo shoves its target away from the caster through
`WorldBroadcaster.Knock`, whose only caller before this was a GM command — the mechanism existed with no gameplay
trigger. Six classes' ladders do it: `Shield Sweep` (Guardian), `Clobber` (Barbarian), `Ferocious Smack` (Bear
Shaman), `Batter Aside` (Conqueror), `Stagger` (Halberdier), `Volley Backwards` (Ranger). The victim's
`KnockbackResistance` is consulted, which is what a resisting buff now moves (#699).

The other categories are deliberately NOT listed in `ComboEffect`: a token there is a claim that something happens
when the combo lands, and naming one we do not act on would read as implemented.

### What a player's FEATS do to their combos

Two GameAction kinds in the feat catalog name a combo, and they are not the same statement — see
[feats.md](feats.md) for their decode. Both are resolved once at world entry into `WorldEntity.ComboBonuses`
(`comboId → ComboFeatBonus`), the same shape and for the same reason `SpellBoosts` is: a swing must not turn into
a query.

| kind | what it modifies | where it lands |
| --- | --- | --- |
| 53250 `ChangeInheritedWeaponAttack` | one WEAPON ATTACK of the combo — its opener or its finisher | that step's swing damage and its critical roll, in `CombatService.SendNormalAttackResponse` |
| 53209 `UpgradeCombo` | the combo as a WHOLE | the finisher's SPELL damage, in `ComboFinisher.Deliver` — which is where this model puts a combo's damage |

So `Fierce Opening` (+30% on every combo's opening attack) makes the first swing of a chain hit harder, and
`Brutal Blows` (+400 crit rating on Overreach's finisher) makes its last swing crit more often, while `Hexer`
(+1.00% on the Blood for Aid combos) raises what the finisher's spell lands for. A step that is neither the opener
nor the finisher takes neither, and a **one-step combo is both** — the two bonuses add.

⚠️ **The two kinds' units differ** and the difference is the client's own: 53250's damage column is a WHOLE
percent and 53209's is hundredths of a percent. They are normalised to hundredths in the repository query, so
nothing downstream has to remember which is which. `CombatResolver.ApplyPercentBonus` is the one place a percent
is applied, shared with the spell-line boost (#590).

`ChangeInheritedWeaponAttack`'s other two axes have no consumer yet and are deliberately not faked: the HATE
column needs a threat model we have none of (it is 2 rows in the whole catalog), and the damage-TYPE column is
zero on every shipped record. Tracked in Gitea #697 along with the proc and per-spell-stat tables.

## NPC combos — the WEAPON picks the generic row, the ARCHETYPE picks the class one

Retail's mobs combo too, and constantly: **484 of the corpus's 840 combo swings are `combo_npc_*` rows**, from 150
distinct NPC actors. They arrive only as a server-to-client `Combat:Melee` ack — the AI drives them, so there is no
request behind one — and they ride the same `combo_id` field a player's own swing uses.

**Which combo is decided by the weapon, not by the creature.** Resolving each captured swing's actor handle against
that capture's own `FA015807` spawns (`--report combo-swings`) puts a name and a worn-item list behind all 484, and
the grouping separates with no crossover: every `combo_npc_generic_polearm` actor holds a polearm, every `_2hb` one
a two-handed blunt, every `_dualdaggers` one a dagger in **both** hands. One NPC *type* with two loadouts settles
it outright — Bat Pict Mauler template 8799 (polearm) swings `generic_polearm` while template 13960 (club) swings
`generic_1hb`.

The weapon class comes from **`Items.SearchCategory`**, the client's own catalog value, and the shipped catalog
separates the classes perfectly (every 30011 row's name carries the `1hEdge` token and no other, and so on):

| slot 6 (main hand) | slot 8 (off hand) | combo |
|---|---|---|
| 30011 1h edge | 30005 shield | `combo_npc_generic_1he_shield` |
| 30011 1h edge | 30011 1h edge | `combo_npc_generic_dual_1h` |
| 30011 1h edge | — | `combo_npc_generic_1he` |
| 30013 1h blunt | (any) | `combo_npc_generic_1hb` |
| 30012 2h edge | — | `combo_npc_generic_2he` |
| 30014 2h blunt | — | `combo_npc_generic_2hb` |
| 30015 dagger | 30015 dagger | `combo_npc_generic_dualdaggers` |
| 30015 dagger | — | `combo_npc_generic_dagger` |
| 30016 polearm | — | `combo_npc_generic_polearm` |

That is the whole `combo_npc_generic_*` set — nine rows — and the off hand only ever refines the one-handed cases,
which is why it is consulted there and nowhere else. Slot 6/8 are the weapon slots: across the shipped spawn set no
weapon-class item appears in any other slot (slot 7 is the belt).

**Not every swing combos.** 427 of the 1,504 captured s2c `Combat:Melee` acks whose actor is a named NPC carry a
combo — 28.4% — so `WorldSimulation.ApplyNpcAttack` rolls per swing (`NpcWeapon.MeleeComboShare`) rather than
combo-ing every time. `Combat:Unarmed` is **0 of 769**, which needs no rule of its own: an animal has no weapon, so
the lookup returns nothing. The id is resolved once per NPC at zone load (`WorldEntity.MeleeComboId`), beside
`RangedAttacker` and for the same reason — that is where the equipment rows are already `Include`d.

### The class families (#675)

Retail also swings per-archetype rows on the same mobs, at roughly the same rate as the generic ones
(`combo_npc_guardian_polearm_1_sweep` ×31 against `generic_polearm` ×29 on the same Pillager). Two things about
them are settled and one is not.

**`Combos.Criteria` is the WEAPON CLASS the row requires**, in the same numbering `Items.SearchCategory` uses.
That is what turns the families from names into a lookup, and it holds over all 744 catalog rows — wherever a
combo's name carries a weapon token, its `Criteria` is that weapon's, with no contradiction:

| `Criteria` | weapon | `SearchCategory` | families at that criteria |
|---:|---|---:|---|
| 23 | 1h edge | 30011 | generic, **guardian**_shield |
| 25 | polearm | 30016 | generic, **guardian**_polearm |
| 26 | bow | 30017 | **ranger**_bow — *no generic row* |
| 27 | 1h blunt | 30013 | generic |
| 28 | 2h (blunt **and** edge) | 30012 | generic, **barbarian**_2h, **conqueror**_2h |
| 29 | 2h blunt | 30014 | generic |
| 30 | dagger | 30015 | generic |
| 32 | crossbow | 30018 | **ranger**_crossbow — *no generic row* |
| 52 | dual-wield | — | generic, **barbarian**_dual, **conqueror**_dual |
| 0 | none | — | assassin, bear_shaman, dark_templar, hox, and the **barbarian** heimdall fatalities |

**The trailing number is `Combos.Level`, a CEILING.** Across the 30 captured class-family swings no actor ever
swings a row above its own level, and a level-75 mob still swings the rank-1 row — so a higher-level NPC's
eligible set *contains* a lower one's rather than replacing it.

### ⭐ The client STATES each row's class — `npcclass`

The combo XML carries an `npcclass` attribute, imported as `Combos.NpcClass`. It is present on **exactly the 68
`combo_npc_*` rows and on nothing else**, and its values are `ConanClass` verbatim: 18 Barbarian, 20 Guardian,
22 Conqueror, 29 Bear Shaman, 31 Dark Templar, 34 Assassin, 39 Ranger, 43 Herald of Xotli — plus **`-1` for the
nine generic rows**, which is a statement ("belongs to no class") rather than an absence.

⚠️ **It disagrees with the NAME on four rows, which is the reason to carry it:**

| row | the name says | `npcclass` says |
|---|---|---|
| `combo_npc_conqueror_dual_1_life_reaper` | Conqueror | **31 Dark Templar** |
| `combo_npc_conqueror_dual_1_blood_for_aid` | Conqueror | **31 Dark Templar** |
| `combo_npc_heimdall_fatality_1` / `_2` | a family of its own | **18 Barbarian** |

⭐ The corpus corroborates the second one **3 of 3**: every actor that swung a heimdall fatality (Argus the Thief,
Crocodile Pict Raider, Wharf Rat Thug) also swung barbarian rows. `combo_npc_blackone_execute` likewise files
under 43 Herald of Xotli, and one row — `combo_npc_assassin_death_whirlwind` — carries no `npcclass` at all.

`ComboService.NpcClassComboIds` uses it as a **veto** over the name prefix rather than as the selector: a row
whose stated class contradicts the family is dropped, a row that states nothing is kept. That is a correction and
never a narrowing, which matters because of that one silent row.

⚠️ **And it reframes the weapon evidence.** The four families that carry NO weapon criteria at all — `assassin`,
`bear_shaman`, `dark_templar`, `hox` — are exactly the ones the client does not gate on a weapon. So `dagger →
assassin` is a CAPTURE correlation (8 of 8 dagger-armed templates) wearing the shape of a weapon rule, and a
dagger-armed non-assassin would swing the wrong family. `polearm` / `bow` / `crossbow` are on firmer ground: their
rows do state criteria 25 / 26 / 32 and at each of those the class family is unique.

### ⭐ The archetype is HARVESTED per template (#675, closed)

**It is in nothing we hold.** Every column cuts across the eight observed families — `Community`, `MonsterData`,
`SuperFamily`, `NpcType`, `Race`, `VoiceSet`, `CarsGroup`, `Flags`, `Bcc` (constant 12488 on every one) and
`ProfessionType` (null on all but one actor); `MonsterData` 140326 alone carries barbarians, guardians and
assassins. A purity sweep of **every** `NpcStatCore` column against the class-family templates beats the
majority-class baseline only where cardinality buys it for free (`TemplateId`, 33 distinct values, scores 1.000).
The client ships **no NPC-template catalog** to read it from, and `NpcSpells` — 63 rows over 33 of 1,194
templates, so a spell id is very nearly a template id — is the same artefact rather than a route.

**So it is harvested, and the corpus says it is a per-TEMPLATE property with no exceptions.** Every captured NPC
combo swing carries the catalog id; `Combos.NpcClass` states that row's class; the module tallies the classes per
template. Over the **36** templates the corpus catches swinging a class family, **not one votes for two classes**:

| the evidence | why it is decisive |
|---|---|
| Hired Bodyguard (15441) swings `guardian_shield_*` **and** `guardian_polearm_*` | one class, two weapons — so the class is not the weapon |
| Poacher (8848) swings `barbarian_2h_*` **and** `barbarian_dual_*` | likewise |
| Argus the Thief / Wharf Rat Thug swing `barbarian_2h_*` **and** the `heimdall_fatality` rows | which the client files as `npcclass=18` Barbarian |
| `Pillager` **8756** is a Guardian, `Pillager` **14040** a Dark Templar | so it is per TEMPLATE, not per NAME |
| `Scavenger Lackey` **12137/14330** Conqueror vs **12141/14331** Assassin | likewise, and in the same zone |

⭐ **And it explains the shield refutation.** `combo_npc_guardian_shield_*` is swung by only 3 of the 6 captured
shield-bearing templates because the other three are not Guardians — `Vanir Champion Elite` wears a shield and
swings the *Barbarian* fatality rows. Read as a weapon rule the bucket was contradictory; read as an archetype it
is not.

The harvest is `--only npc-combo-class` → `NpcStatCore.ComboClass`, and it ships **36 templates over 7 classes**
(Bear Shaman is the one the corpus never catches). A template no capture ever caught keeps NULL.

**At runtime the class comes from the column and the weapon only narrows within it**, through the row's own
`Criteria` (`NpcWeapon.MeetsComboCriteria`). ⚠️ One widening, and it is capture-driven: on the generic set
`Criteria` 28 is *2h edge*, but retail swings the criteria-28 CLASS rows (`barbarian_2h`, `conqueror_2h`) with
two-handed BLUNT weapons — LargeWarclub, Warhammer, HeavyBludgeon, SledgeHammer, BattleMaul — so on a class family
28 reads as *any two-hander*. Refusing 2h blunt there would silence exactly the mobs the archetype came from.

**The weapon fallback stays** for the ~97% of templates with no harvested archetype, and it is unchanged: the two
buckets where the class family at that weapon criteria is unique, measured per SPAWN rather than per type (one
handle in one capture holds one loadout):

| weapon | family | evidence |
|---|---|---|
| dagger | `combo_npc_assassin_*` | all 31 assassin-family swings are by dagger-wielders; all **8** dagger-armed templates swing it |
| polearm | `combo_npc_guardian_polearm_*` | 61 swings; all **5** polearm-armed templates swing it |

Both paths are level-gated and chosen against the generic row at `ClassComboShare` — the corpus splits 92 class /
65 generic over exactly those two weapons.

**Ranged needs no archetype at all, and is wired since #676.** `ranger` is the only family at criteria 26/32,
there is no `combo_npc_generic_bow` to fall back on, and all 57 captured ranged combos are ranger rows. A bow
draws from `combo_npc_ranger_bow_*` and a crossbow from `combo_npc_ranger_crossbow_*`, at the rate retail shows
for a shot — **57 of 418 acks, 13.6%**, against melee's 28.4%. A THROWN weapon stays plain: no
`combo_npc_*_thrown_*` row exists.

What had to land first was the CARRIER, because a bow combo on a melee ack would have been wrong in a new way —
see `docs/subsystems/combat.md`.

Gated by `NpcComboTests`, which locks the catalog premise, the mapping, the coverage over the shipped spawn set,
the `Criteria` decode (including the 28 widening), the level ceiling, the two weapon-answered buckets, the
harvested archetype resolving the three the weapon cannot, the shield-bearing Barbarian, and that every shipped
`ComboClass` is a class the catalog holds — and that the id reaches the wire in `combo_id` with nothing else about
the ack moving.

Open work — spellweaving polish — is tracked on **Gitea #141**.

⚠️ **The rung's own stat payload is not applied yet.** The ladder's `Modify BaseSpellDamage`, its
`Modify NaturalEnergyRegeneration -15000` and the `SpellLineStatModifier` splash package are ordinary
GameAction elements on the rung's own record, so they land when the generic engine walks a `CastNano` chain
and evaluates criteria (**#784** / **#791**) — not through anything bespoke in `SpellweaveService`. The same
`-15000` regen is also the mechanism behind the energy bar draining: retail does not stream the drain, it
sets the regen rate negative and lets the client simulate.

## Every path that mints an NPC has to stamp its combat kit (#837)

Four properties decide how an NPC fights and nothing else derives them: `RangedAttacker`, `AttackRange`,
`MeleeComboId` and `ClassComboIds`. They come from the kit the NPC is wearing, and the only code that knows the
kit is the code that mints the entity — so every mint site has to stamp them, and for a long time only
`ZonePopulationLoader` (the placed spawns) did.

`AmbientPoolService` (#363 spawn-area pools) stamped none, and `EncounterService` (the #502 boss swap, its lure
and #528 quest ambushers) stamped none. Every pooled or encounter NPC therefore fought unarmed with no combo,
whatever its type or its outfit said.

⚠️ **It was invisible, which is why it lasted.** No ambient pool currently resolves to an archer — the bow
carriers are their own templates — and every captured `Poacher` kit is melee, so the beach crowd fought correctly
*by luck rather than by construction*. It would have surfaced as a one-zone bug report the first time a pool
resolved to an archer, long after the cause.

The ambient pool resolves one combo set **per outfit**, at pool load: `SpawnMember` cannot await (`Replenish` is
an iterator), and a pool of eight draws from a handful of harvested kits, so this is a few lookups per pool
rather than one per mob. ⚠️ The outfit and its combo set are picked with **one** index — deriving them from two
separate counts would let a mob swing a combo its weapon is not holding the moment the two drift.

The encounter path has no outfit at all (`BuildFromType` is called with none), so its weapon-derived half
correctly answers "unarmed" and the value is in the class half: `NpcStatCore.ComboClass` is a harvested archetype
that needs no equipment to say which family a mob swings.

`NpcCombatKitChokePointTests` is the gate — a per-file census of `WorldEntity.ForNpc` callers, coarse on purpose,
because what is worth catching is a NEW path starting to mint NPCs. It names the missing property: reverting one
stamp fails it with *"AmbientPoolService.cs (no ClassComboIds)"*. That specificity matters here, since #838 wired
the two weapon-reach properties into the ambient pool and left the two combo ones behind — a half-fix is exactly
what this catches.

## A rank table's increments are not book spells (#854)

The level path was offering a Conqueror **twenty** spells at level 20 — `Colossus of War (1)` … `(20)`, ids
3275588–3275639. They are the stacking increments of a spell simply named `Colossus of War` (3275545/3275640).

⭐ **Structurally this is #813's rank TABLE, one link over.** The carrier is in **no** stacking line
(`GuiStackingLine` 0) and carries more than one `CastNano` edge INTO one line — here twenty edges into line
22200. That is the same shape as the `Furious Inspiration` header that was putting all ten of its ranks on the
buff bar at once; #813 stops it being *applied*, and this stops it being *taught*.

⛔ **The rule is structural, not a name match.** #854 asks for exactly that and warns against the alternative: a
rule keyed on the `(N)` suffix is the kind of string heuristic this codebase has repeatedly had to unwind.

⭐ Checked against the same oracle the combo (#298) and effect (#847) exclusions use — the captured known-spell
lists: of the **383** spells the rule catches, **zero** appear in any captured spellbook, while **210** were
being offered by the level path. `--report spell-grant-check` is unchanged by it (97/96/5/0/3 matched, EXTRA 0,
DROPPED 0), which is what "zero in a captured spellbook" predicts.

⚠️ **The carrier must be in no stacking line, and that narrowing is load-bearing rather than tidy.** A carrier
that is itself in a line is a rank *ladder* rung, not a table header. Without the clause the rule also swallows
`Bleeding Puncture I..IV` (Ranger, levels 5/36/52/64), which read as an ordinary learnable line — the broader
form catches 437 spells where the narrow one catches 383. It is also what keeps `Flesh to Worms (Rank 1)` safe,
which is the trap #847 is shaped around.

⚠️ **What this does not have is a positive Conqueror reading.** The oracle covers four characters of four
classes and none of them is a Conqueror, so the support is an absence. What makes that acceptable is that the
rule is structural and its one false-positive candidate was found and removed by *narrowing the rule*, not by
listing an exception.
