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

**Melee** (`Captures/Hakotep_hox_login_gameplay0` — a HoX executed 3 two-step combos):
- A combo is a **sequence of `NormalAttack` (BehaviorControlCenter, ActionType 0xF)** c2s actions; the pairs group
  by a shared trailing id.
- Each `NormalAttack` carries a **direction code** in its variable payload (NB offset 22 is the payload LENGTH, per
  `SendUnarmedAttackResponse`, so the direction is AFTER offset 26 — the earlier "direction = 0x12/0x13/0x15/0x16"
  read was actually the length field). **TODO: pin the direction code → `ComboInput` map** from this capture.
- Overlaps the `#20` "Real Combat" directional-arc decode (per-direction mitigation).

**Spellweaving** (`Captures/NecroL80_mage-necromancer_login_spellweaving`):
- c2s inputs are NOT BCC swings; they ride `0x2000` carrier packets carrying small ids (`0xaa11/aa12/aa13/aa57…`)
  — consistent with the activate / add-component / deactivate actions, not a matched sequence.
- **TODO: decode the three actions** — spellweave ACTIVATE (enter trance + immobilise), ADD-COMPONENT (the
  `0xaaXX` ids = the spell components; each blends stats/effect), and DEACTIVATE (fire the accumulated weave +
  the backlash roll).

## Remaining to implement (#2)

1. **Melee**: pin the direction-code → `ComboInput` map (Hakotep dump). Open question: likely **activated-combo**
   (the client picks the combo, then the directions confirm it) rather than free-chaining — resolve from whether a
   c2s activation packet references the combo id before the swings. Then a per-player FSM that matches the swing
   inputs against `GetPerformableCombosAsync` and fires the finisher (the s2c `ApplySpell` effect near the cluster).
2. **Spellweaving** is a SEPARATE subsystem (free-form, not a catalog match): an activate→trance→accumulate→
   deactivate state machine that immobilises the caster, sums the added components' stat/effect contributions,
   and on deactivate fires the blend + rolls backlash. Decode the 3 actions first.
3. In-client validation for both (drive the retail client, perform a combo / a weave, confirm the outcome).
