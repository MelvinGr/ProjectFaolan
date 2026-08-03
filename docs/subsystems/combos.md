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
  `action` (69 rows) is the fifth marker and the only one with **no** direction after it — a placeholder for combos
  whose input the catalog does not model (bow attacks, emotes, `PowerAttack`). The 160 of 744 rows whose `Actions` is
  blank are the same shape of thing and remain unexplained.

## Wire decode

**Melee** (dumps: `Hakotep_hox_login_gameplay0`, `Hoxxling_mage-heraldofxotli_combos` [HoX], plus
`AssassinL23_rogue-assassin_login_combos` [Assassin, incl. a few DELIBERATELY FAILED combos]):
- A combo is a **sequence of `Combat:Melee` (BehaviorControlCenter, `BehaviorId` 0xF)** c2s actions. **No separate
  activation packet** precedes the swings (checked the 3s before a combo's first swing: only heartbeat + move) —
  the swings ARE the input, not an "activate combo X then confirm" flow.
- Swing payload (after the offset-i+18 LENGTH; NB the earlier "direction 0x12/0x15" read was that length field):
  `[08][animByte][2B marker][u32 counter][u32 cid = 0x300038XX][tail]`.
  - **`cid`** (offset 8) groups the swings of one combo attempt and REPEATS per combo-type within a session (the
    Assassin's `b6`/`cb`/`9a` recur). But it does NOT cleanly encode the catalog combo id — the cid low-byte
    matched the combo-id low-byte for the HoX (`b0`=Molten Steel Slash II, `bf`=Pillar I, `f1`=Body/Spirit Wrack I,
    `fa`=Hell Strikes II) but FAILED on the Assassin (`b6`/`cb`/`9a`/`84` have no such combo) → coincidence, it's a
    client-side handle.
  - **`animByte`** (offset 1) varies per swing but is **class/weapon-relative, not a class-independent direction
    code**: HoX uses `b2`/`b3`/`b4`, Assassin uses `c0`/`c2`/`c4`. For the HoX `b2/b3/b4` lined up with `UL/UR/UP`
    (matching Pillar `UL,UR,UP`, Molten Steel Slash `UL,UR`, Hell Strikes `UL,UP`), but the Assassin (whose combos
    also use `LL`/`LR`) only surfaced 3 byte values, so it reads more like a **weapon swing-animation index** than a
    raw direction. `[08][animByte][f105]` marks the FIRST swing of a combo; continuations carry a class marker
    (`c702` HoX / `8403` Assassin).
- **Not recoverable from packets alone**: the `cid → combo` and `animByte → ComboInput` mappings are computed
  client-side (the client derives the `cid` and selects the swing anim per (combo, direction, weapon)), so
  pinning them needs client-side Ghidra RE. `ComboActionParser` consumes the map once it exists.

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
  (full 8910 for the necro), with a parallel `0xe9f24184` "Spellweaving Cost" gauge (`0x37E68C`). When energy hits 0:
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

Open work — melee combo execution (**Gitea #2**) and spellweaving polish (**Gitea #141**) — is tracked on those issues.
