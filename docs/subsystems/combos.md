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
- **`ClassCombo`** access map — `(Class, ComboId, Level)`, from **two** sources, **436 rows**:
  - the `Combos` name prefix (`CaptureAbilityImporter.DeriveClassCombosAsync`): the 8 literal class-name prefixes =
    the 8 archetypes that have combos. Validated by Hakotep's HoX `0x2076` combo book. 292 rows; casters empty.
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
  mirrors it).
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

Gated by `ComboServiceTests` (the runtime) and `ComboWireTests` (the wire claims, against the captured swings in
the `combo-swings.bin` fixture).

Open work — combo cooldowns (`Combo.Cooldown`), the combo-completed on-screen feedback, and NPC combos on the
server's own AI — is tracked on **Gitea #139**; spellweaving polish on **Gitea #141**.
