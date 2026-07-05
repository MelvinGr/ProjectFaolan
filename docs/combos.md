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

- **`Combos`** catalog — RDB 1010206 (plaintext XML). `Name` is an engine name `{Class}_{level}_{tags}_{Display}_{Rank}`,
  `Level` = unlock level, `Actions` = the required input (melee: `UR,UP`; weave/reactive: markers like `MagicCombo`/
  `Magic_Reactive`), `GameActionId`, `ParentId` (= the weapon-VARIANT group root, NOT the input chain), `Criteria`
  (weapon/state).
- **`ClassCombo`** access map — `(Class, ComboId, Level)`, derived from the `Combos` name prefix
  (`CaptureAbilityImporter.DeriveClassCombosAsync`): the 8 literal class-name prefixes = the 8 archetypes that have
  combos. Validated by Hakotep's HoX `0x2076` combo book. 292 rows; casters empty.
- **Grant** — `GetClassCombos` is **rank-superseded** per family (`ComboFamilyKey`, the combo twin of the spell-bar
  family collapse): one combo per line at the highest qualifying rank (a lvl-24 HoX → Hell Strikes II, not I+II —
  matches the retail `0x2076` book). `GrantCharacterCombos` + the level-up dings emit it.
- **`ComboActionParser`** (`Faolan/GameServer/Combat/ComboInput.cs`) — normalises all 33 `Actions` tokens into the
  `ComboInput` enum (4 quadrants + Up/Head/Thrust); returns false for the non-directional markers (spellweaving /
  reactive). Validated against all 130 distinct `Actions` (109 directional, 21 non-directional, 0 unknown).
- **`ComboService`** — `GetPerformableCombosAsync` = the superseded, directional combos as
  `PerformableCombo(id, name, ComboInput[] sequence, gameActionId)`.

## Wire decode (IN PROGRESS)

**Melee** (dumps: `Hakotep_hox_login_gameplay0`, `Hoxxling_mage-heraldofxotli_combos` [HoX], plus
`AssassinL23_rogue-assassin_login_combos` [Assassin, incl. a few DELIBERATELY FAILED combos]):
- A combo is a **sequence of `NormalAttack` (BehaviorControlCenter, ActionType 0xF)** c2s actions. **No separate
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
- **Blocked in-packet**: pinning `cid → combo` and `animByte → ComboInput` reliably needs **client-side RE**
  (Ghidra) — how the client derives the `cid` and selects the swing anim per (combo, direction, weapon). Overlaps
  the `#20` "Real Combat" directional decode. `ComboActionParser`/`ComboService` are ready to consume the map once
  pinned.

**Spellweaving** — DECODED (`Captures/NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving`, a necro
that ate 3 potions/food then wove Arcane Renewal + Arcane Surge + Death God then stopped):
- **Activate / deactivate = a TOGGLE**: c2s method `0x205a`, body `[u32 0x000000fc]` (same value both times, like
  the mount toggle) — enter the trance (immobilise) on the first, fire + leave on the second.
- **Add-component**: c2s `0x2000` carrier, IIR sub `0x54C02089` = **`CombatReactiveIIR_c`** (reader `FUN_0082c720`,
  docs/iir-readers-combat-world.md) — the same family as the catalog's `*_Reactive`/`MagicCombo` Action markers.
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
- `0x2085` (~2s heartbeat) and food/potion `RightClickItem` (BCC ActionType 0x0e) are NOT part of the weave.

## Remaining to implement (#2)

1. **Melee**: pin the direction-code → `ComboInput` map (Hakotep dump). Open question: likely **activated-combo**
   (the client picks the combo, then the directions confirm it) rather than free-chaining — resolve from whether a
   c2s activation packet references the combo id before the swings. Then a per-player FSM that matches the swing
   inputs against `GetPerformableCombosAsync` and fires the finisher (the s2c `ApplySpell` effect near the cluster).
2. **Spellweaving** is BUILT for the Necromancer and (except the energy bar) works in-client — see
   `docs/combos-spellweaving-handoff.md` §5 and **Gitea #141** (`needs-testing`) for the full state, code map, and
   the open items. Top open bug: the **energy bar drops straight to 0** on weave start (the `0x200E` stream/scale
   needs a frame-by-frame diff vs the capture). Also open: the `componentIndex`→effect table + backlash roll
   (undecoded), authoritative DoT death, trance immobilisation, and the other 3 caster profiles.
3. In-client validation for melee (drive the retail client, perform a combo, confirm the outcome).
