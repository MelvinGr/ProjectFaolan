# Combos & Spellweaving

Two sibling mechanics — both build up an outcome from a series of player inputs — but they differ in a key
way: melee combos MATCH a fixed catalog sequence, while spellweaving is FREE-FORM composition.

| | Melee combo | Spellweaving (caster) |
|---|---|---|
| Structure | a FIXED sequence from the catalog (`Combos.Actions`) | FREE-FORM: the player composes the weave live |
| Input kind | directional swing key (Upper-Left/Right, Lower-Left/Right, Up, Head, Thrust) | adding a spell "component"; each contributes **extra stats / an effect** to a blended result |
| Timing | rapid succession (chain the keys) | paced adds while in a trance |
| Outcome | the specific combo's finisher (damage / effect) | the ACCUMULATED blend, fired on deactivate |
| Risk | — | **backlash**: chance of ill-effects scaling with the weave's power |
| Who | the 8 combo archetypes (soldiers, rogues, Bear Shaman, Herald of Xotli) | the pure casters (Necromancer, Demonologist, Priest of Mitra, Tempest of Set) |

A class does **one or the other**. That is why `ClassCombo` (melee) leaves the 4 pure casters empty — they
weave instead. (Bear Shaman & Herald of Xotli are melee/hybrid, so they have `Combos`.)

**Spellweaving mechanic** (https://aoc.fandom.com/wiki/Spellweaving — fetch via the fandom API, the page is
Cloudflare-gated for curl/WebFetch: `api.php?action=parse&page=Spellweaving&prop=wikitext&format=json`):
activate the spellweave ability → enter a **trance** (immobilised, can ONLY add components) → add up to a
level-capped number of spells (max 20), each blending its stats/effect into the result → **deactivate** to
fire the accumulated weave at the target and leave the trance. Because it is player-composed there is no
fixed catalog to match (unlike melee) — the server accumulates the added components' contributions and
resolves the blend + a backlash roll on deactivate.

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

The yellow bar is not drawn from the `0x200E` packet alone — it is a client-side simulation, which is why sprinting
drains the bar with no server traffic at all and only the server-driven WEAVE drain shows up on the wire. The client
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
simply where one landed. The emit is gone; a weave pushes nothing here, and its real effect (the
`componentIndex -> effect` blend) is still open on #141.
- `0x2085` (~2s heartbeat) and food/potion `UseItem` (BCC `BehaviorId` 0x0e) are NOT part of the weave.

## Implementation (spellweaving, Necromancer)

The Necromancer weave is built end-to-end and byte-exact (golden tests `SpellweaveCodecTests` +
`BehaviorAnimationsTests`). Code map:

- `SpellweaveService` (`Faolan/GameServer/Combat/SpellweaveService.cs`) — the weave FSM + the trance-meter /
  energy loop. Session state on `NetworkClientSession`: `IsSpellweaving`, `WeaveComponents`, `SpellweaveBuffStack`,
  `Energy`/`EnergyMax`/`IsSprinting`/`EnergyLoopActive`. It reaches the wire through one narrow port the listener
  implements, `ISpellweaveOutbound`: `ApplyBuff` (the trance buff and each component's own buff, via the shared
  `ApplySpell` path since #274), `RemoveBuff` (rank-1 end-twins, relayed to viewers so the pose ends on their
  screens too), `SendFeedback`, `SendSprintBar` (`0x200E`), `EnsureEnergyLoop`, and `IsMounted` — the trance roots
  the caster and plays a kneeling pose, so opening one from the saddle is refused with an on-screen line rather
  than pinning a rider mid-gallop.
- `CombatReactiveCodec` (`Faolan.Core.Network/Codecs/Combat/CombatReactiveCodec.cs`) — reactive-list, stat-gauge
  and buff-data builders.
- `BehaviorAnimations` (`Faolan.Core.Network/Codecs/Combat/BehaviorAnimations.cs`) — the trance /
  component / EXIT BCC animations (BCC `0x642CD3D6` is the generic behaviour channel, shared with combat).
- `SpellweaveCatalog` (`Faolan.Core.Network/Constants/SpellweaveCatalog.cs`) — the per-class `SpellweaveProfile`
  (Necromancer only) + `EnergyMaxForClass`. Its `FireStages` / `CapturedFireRating` are **gone** — see the
  rating-quad note above.
- `GameServerListener.Buffs.cs` — `SendSprintBar` (`0x200E`) + the persistent energy loop (`RunEnergyMirror`:
  drain-on-sprint / regen-when-idle / step-aside-while-weaving; the sprint drain itself is client-side, the server
  mirrors it). The loop also pushes `CharacterStatId.Energy` on the retail cadence and owns the sprint-path
  `Exhausted` apply/remove — see the four-stat table above.
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
