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

The map (`ClassCombos`) has three sources. The second was added in #296 and the third in #581, each because the ones before it refuse to guess:

1. **The combo's NAME prefix**, for the class-named ones (`Guardian_`, `Ranger_`, …) — `CaptureAbilityImporter`,
   plus captured action bars for direct `(class, combo)` evidence.
2. **The stacking line of the SPELL the combo drives.** A combo's `GameActionId` is a spell id, and a class-encoded
   `Spell.GuiStackingLine` (`ConanClass × 1000 + lineIndex`) names the class. This resolves the FEAT-TREE prefixes
   the name heuristic cannot — `Reaver_` → Barbarian, `Liberator_` → Conqueror, `Halberdier_` → Guardian —
   and it agrees with source 1 on **257 of 257** combos where both speak.

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

Gated by `Faolan.Tests/ClassComboMembershipTests.cs`, which pins all of it: the stacking-line rule never
contradicts source 1, each archetype combo is mapped to exactly its archetype's three classes, and the archetype
spells go to those three or to nobody.

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
- **`ClassCombo`** access map — `(Class, ComboId, Level)`, from **three** sources, **668 rows**:
  - the `Combos` name prefix (`CaptureAbilityImporter.DeriveClassCombosAsync`): the 8 literal class-name prefixes =
    the 8 archetypes that have combos. Validated by Hakotep's HoX `0x2076` combo book. 292 rows; casters empty.
  - the **ARCHETYPE** prefixes in the same pass (#581): `Soldier_` / `Rogue_` / `Priest_` name an archetype rather
    than a class, so each fans out to that archetype's three (`ConanArchetypes`). 51 combos, 153 rows.
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
- **Fire / cancel**: on the 2nd toggle, the s2c `f98e10b3` burst (the weave firing) + trance-buff end-twins + a
  trance-EXIT BCC. Per add: the component's own spell as an immediate self-buff + add-gesture BCC; the Spellweaving(1)
  buff re-applies at an incremented id each picker reappear (stacks ~x2).
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
- `CombatReactiveCodec` (`Faolan.Core.Network/Codecs/Combat/CombatReactiveCodec.cs`) — reactive-list, stat-gauge,
  buff-data and fire-stage builders.
- `BehaviorAnimations` (`Faolan.Core.Network/Codecs/Combat/BehaviorAnimations.cs`) — the trance /
  component / EXIT BCC animations (BCC `0x642CD3D6` is the generic behaviour channel, shared with combat).
- `SpellweaveCatalog` (`Faolan.Core.Network/Constants/SpellweaveCatalog.cs`) — the per-class `SpellweaveProfile`
  (Necromancer only) + `EnergyMaxForClass`.
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
and its debuff are that spell's, and `GameServerListener.ResolveComboFinisher` runs the same two ops a cast does
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

## NPC combos — the WEAPON picks the row

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
| 0 | none | — | assassin, bear_shaman, dark_templar, hox, heimdall |

**The trailing number is `Combos.Level`, a CEILING.** Across the 30 captured class-family swings no actor ever
swings a row above its own level, and a level-75 mob still swings the rank-1 row — so a higher-level NPC's
eligible set *contains* a lower one's rather than replacing it.

**The archetype itself is a per-NPC property and is not recoverable in general.** Everything we hold cuts across
the eight observed families — `Community`, `MonsterData`, `SuperFamily`, `NpcType`, `Race`, `VoiceSet`,
`CarsGroup`, `Flags`, and `ProfessionType` (null on all but one actor). The client ships **no NPC-template
catalog** to read it from (RDB 1000020 has no record for the combat actors' template ids), and all 31
combo-swinging templates have **zero `NpcSpell` rows**, so the two routes #675 named are both dead.

**Two buckets are answered anyway, because they have exactly one class family.** Measured per SPAWN rather than
per type — one handle in one capture holds one loadout, which is the cut that matters:

| weapon | family | evidence |
|---|---|---|
| dagger | `combo_npc_assassin_*` | all 31 assassin-family swings are by dagger-wielders; all **8** dagger-armed templates swing it |
| polearm | `combo_npc_guardian_polearm_*` | 61 swings; all **5** polearm-armed templates swing it |

Those two are shipped (`NpcWeapon.ClassComboFamily` → `ComboService.NpcClassComboIds`), level-gated, and chosen
against the generic row at `ClassComboShare` — the corpus splits 92 class / 65 generic over exactly these two
weapons.

⚠️ **The rest are refused, and the shield case is refuted rather than unsampled.** `combo_npc_guardian_shield_*`
is swung by only **3 of the 6** captured shield-bearing templates — the three Vanir ones swing generic only — so
a shield does not make a guardian. Two-handers and dual-wielders carry two or three candidate families
(`barbarian` / `conqueror` / `hox`) and no template swings two of them.

**Ranged needs no archetype at all, and is wired since #676.** `ranger` is the only family at criteria 26/32,
there is no `combo_npc_generic_bow` to fall back on, and all 57 captured ranged combos are ranger rows. A bow
draws from `combo_npc_ranger_bow_*` and a crossbow from `combo_npc_ranger_crossbow_*`, at the rate retail shows
for a shot — **57 of 418 acks, 13.6%**, against melee's 28.4%. A THROWN weapon stays plain: no
`combo_npc_*_thrown_*` row exists.

What had to land first was the CARRIER, because a bow combo on a melee ack would have been wrong in a new way —
see `docs/subsystems/combat.md`.

Gated by `NpcComboTests`, which locks the catalog premise, the mapping, the coverage over the shipped spawn set,
the `Criteria` decode, the level ceiling, the two answered buckets and the ones deliberately refused — and that
the id reaches the wire in `combo_id` with nothing else about the ack moving.

Open work — spellweaving polish — is tracked on **Gitea #141**.
