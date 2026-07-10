# Handoff — Combos & Spellweaving

Detailed handoff for the AoC combo (melee) + spellweaving (caster) systems. Companion to `docs/subsystems/combos.md`
(the design overview). This file is the "where we are / how to continue" for a fresh session.

## 0. Status at a glance

Tracked as **Gitea #141** (`needs-testing`). Full session write-up is in the #141 comment thread.

| Piece                                                                   | State                                           |
| ----------------------------------------------------------------------- | ----------------------------------------------- |
| `ClassCombo` table/grant/superseding + `ComboActionParser`/`ComboService` | ✅ committed (melee data side)                 |
| **Spellweave wire decode** (activate/adds/fire/energy/DoT/cancel)       | ✅ DECODED (`Captures/scripts/decode_spellweave.py`)    |
| Activate response: 3 trance buffs + reactive element LIST + trance-pose BCC | ✅ built, byte-exact, works in-client        |
| Component picker (pick→hide→reappear ~8s) + per-add buff + add gesture BCC | ✅ built, works in-client                     |
| Stacking Spellweaving(1) buff (37E5A8→A9→AA) each picker reappear        | ✅ built, works in-client (x2 overlap)          |
| FIRE burst + CANCEL (buff end-twins + trance-EXIT BCC)                   | ✅ built, works in-client                       |
| Energy bar (`0x200E` DispatchUpdateSprintbar) + `0x37E68C` gauge stream  | ⚠️ built but **drops to 0 in-client** (see §5)  |
| DoT on energy-empty: `Exhausted` (0xB90) + `0x37E690` health drain       | ✅ built, VISUAL only (no authoritative death)  |
| Server energy tracking (persistent regen loop; sprint mirror)            | ✅ built (sprint drain itself is client-side)   |
| componentIndex→effect table + backlash roll                             | ❌ TODO (undecoded — needs a targeted dump)     |
| Other-caster profiles (Demo / PoM / ToS)                                | ❌ TODO (need per-class captures)               |
| **Melee wire decode** (cid → combo, animByte → direction) + execution   | ⚠️ BLOCKED on client Ghidra RE (§3)             |

All wire builders are **byte-exact** (12 golden tests: `SpellweaveCodecTests` + `BehaviorAnimationCodecTests`).
Latest commit: `54e5c428` (energy/DoT/Exhausted/cancel/stacking). The activate/adds/fire base is in `f3d80f9b` /
`87ecff3e`. Melee `ClassCombo` work is in the earlier `91c6ffba`-and-before series.

## 1. The two mechanics (recap)

Both build an outcome from a sequence of player inputs; a class does ONE of them.

- **Melee combo** — a FIXED catalog sequence of directional swings (`Combos.Actions`, e.g. `UR,UP`) in rapid
  succession → the combo's finisher. Classes: the 8 with `Combos` (soldiers, rogues, Bear Shaman, Herald of Xotli).
- **Spellweaving** — FREE-FORM: activate → trance (immobilised) → add up to ~20 components (each blends
  stats/effect) paced ~10-20s apart → deactivate to fire the blend, with backlash risk. Classes: the 4 pure
  casters (Necromancer, Demonologist, Priest of Mitra, Tempest of Set). Wiki (Cloudflare-gated; use the API):
  `curl 'https://aoc.fandom.com/api.php?action=parse&page=Spellweaving&prop=wikitext&format=json'`.

## 2. Data foundation (DONE — do not redo)

- **`Combos`** catalog = RDB 1010206 (plaintext XML). `Name` = engine name `{Class}_{level}_{tags}_{Display}_{Rank}`;
  `Level` = unlock level; `Actions` = required input; `GameActionId`; `ParentId` = weapon-VARIANT group root (NOT
  an input chain — proven: Enraging Strike I 1h/polearm/2h share `ParentId`, differ only by `Criteria`); `Criteria`.
- **`ClassCombo`** (`Faolan.Core.Database/Tables/ClassCombo.cs`) — `(Class, ComboId, Level)` + FK + enum CHECK.
  Derived by `CaptureAbilityImporter.DeriveClassCombosAsync` from the 8 literal class-name prefixes (Guardian,
  Conqueror, DarkTemplar, Barbarian, Ranger, Assassin, BearShaman, Herald) — exactly the 8 archetypes that have
  combos, so casters stay empty. Validated by Hakotep's HoX `0x2076` book. 292 rows. Ambiguous prefixes
  (Soldier*/Rogue* archetype-shared, feat-trees Reaver/Liberator/Lich/…, Slave\_ starters, combo_npc, racials) are
  deliberately EXCLUDED.
- **Grant** — `IDatabaseRepository.GetClassCombos(level, class)` returns the combos RANK-SUPERSEDED by family
  (`ComboFamilyKey` in `DatabaseRepository.cs`, the combo twin of `GetStartingBarSpells`): one per line at the
  highest qualifying rank (lvl-24 HoX → Hell Strikes II not I+II, matching the `0x2076` book). Emitted by
  `GrantCharacterCombos` (`GameServerListener.WorldEntry.cs`) + the two level-up ding paths (CombatService,
  `SendLevelUpDings` in `GameServerListener.WorldEntry.cs`). This REPLACED the class-independent `LevelComboCatalog` (retired to a data reference) —
  which was the bug where a Necromancer re-"learned" Sweep/Slam/Hell Strikes every login.
- **`ComboActionParser`** (`Faolan/GameServer/Combat/ComboActionParser.cs`) — normalises all 33 `Actions` tokens into the
  `ComboInput` enum (UpperLeft/Right, LowerLeft/Right, Up, Head, Thrust); returns false for non-directional markers
  (`action`, `MagicCombo`, `*_Reactive` = spellweaving/reactive). Validated: 130 distinct Actions, 0 unknown tokens.
- **`ComboService`** (`Faolan/GameServer/Combat/ComboService.cs`) — `GetPerformableCombosAsync(class, level)` =
  the superseded, directional combos as `PerformableCombo(id, name, ComboInput[] sequence, gameActionId)`.

## 3. Melee combos — wire decode (⚠️ BLOCKED)

Dumps: `Hoxxling_mage-heraldofxotli_combos`, `Hakotep_hox_login_gameplay0` (HoX),
`AssassinL23_rogue-assassin_login_combos` (Assassin, includes DELIBERATELY FAILED combos).

- A combo = a **sequence of `NormalAttack`** actions: `0x2000` carrier → `BehaviorControlCenterIIR_c`
  (`0x642CD3D6`), ActionType **`0xF`**. **No separate activation packet** (checked the 3s before a combo's first
  swing: only heartbeat + move) — the swings ARE the input.
- Swing payload layout (`b` = 0x2000 sub-payload; `i` = offset of the `0x642CD3D6` sub; length field at `i+18`,
  payload starts at `i+22`):
  `[08][animByte][2B marker][u32 counter][u32 cid = 0x300038XX][tail]`
  - `cid` (payload offset 8) **groups a combo attempt** and repeats per combo-type within a session, but does NOT
    cleanly encode the catalog combo id — low-byte matched the HoX (`b0`=Molten Steel Slash II 0x2EE**B0**, `bf`=Pillar
    I 0x30DC**BF**, `f1`=Body/Spirit Wrack I 0x3989**F1**, `fa`=Hell Strikes II 0x30FD**FA**) but FAILED on the Assassin
    (`b6`/`cb`/`9a`/`84` have no such combo) → coincidence; it's a client-side handle.
  - `animByte` (payload offset 1) varies per swing but is **class/weapon-relative**: HoX `b2`/`b3`/`b4`, Assassin
    `c0`/`c2`/`c4`. HoX `b2/b3/b4` lined up with `UL/UR/UP` (matched Pillar `UL,UR,UP`, Molten Steel `UL,UR`, Hell
    Strikes `UL,UP`), but the Assassin (whose combos also use `LL`/`LR`) only surfaced 3 byte values → it reads more
    like a weapon SWING-ANIMATION index than a raw direction. `[08][animByte][f105]` marks the FIRST swing of a
    combo; continuations carry a class marker (`c702` HoX / `8403` Assassin).
- **Blocker**: `cid → combo` and `animByte → ComboInput` are not recoverable from packets alone. Next step is
  **client-side Ghidra RE** (how the client derives the `cid` and picks the swing anim per (combo, direction,
  weapon)); overlaps `#20` "Real Combat". `ComboService` is ready to consume the map once pinned.

## 4. Spellweaving — wire decode (✅ COMPLETE)

Dumps: `NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving` (the clean ground truth: ate 3
potions/food, then wove **Arcane Renewal, Arcane Surge, Death God**, then stopped) and
`NecroL80_mage-necromancer_login_spellweaving`.

Timeline (clean dump): potions (BCC `RightClickItem` 0x0e) at 82.5/87.0/97.7s → **activate 103.3s** → adds
105.8/115.5/131.1s (~10-15s apart) → **deactivate 141.4s** → fire 141.6s.

- **Activate / deactivate = a TOGGLE**: c2s opcode **`0x205A`** (`GameCharAgentOpcodes.PerformSpecialAction`), body
  `[u32 0x000000FC]`. SAME value both presses (like the mount toggle) — first opens the trance, second fires+closes.
- **Activate RESPONSE (s2c, ~103.8s)** — the currently-MISSING part that stalls our client at "started":
  - **3× trance buffs**: `ApplySpell` (`0x6B87E316`), wire ids **`0x37F29800`, `0x37E5A800`, `0x3969D000`** (the
    `(base<<8)|rank` form; base 0x37F298 etc., rank 0x00). ApplySpell body =
    `[len][6b87e316][EntityClass 0x0000c350][applyTo][u16 0][u32 spellId][EntityClass][applyFrom][data tail]`;
    duration lives at `data+8` in centiseconds (see `ApplySpell` in `GameServerListener.Buffs.cs` + `ActiveBuff`). First
    buff verbatim: `000000506b87e3160000c3500802e5d4000037f298000000c3500802e5d40000…`.
  - a **`CombatReactiveIIR`** (`0x54C02089`) s2c reactive element LIST (the addable weave elements), and a trance-pose
    **BCC** animation (`0x642CD3D6`, action 42041 — `BehaviorAnimationCodec.SpellweaveTrance`).
- **Energy bar (the yellow "sprint" bar) drains during the weave** — this is the visible trance gauge, and it is a
  SEPARATE universal pool from the class-resource "stamina" stat (necro class stamina = 0; the energy pool exists
  anyway). TWO packets per tick, same token:
  - **`DispatchUpdateSprintbar` (`0x200E`)** — the packet that actually MOVES the bar. Body `[u32 energy][u8 01]`,
    sender `GameCharAgentProject/0x54` → `GameCharInterfaceProject/instance`. Energy full = **8910** for the necro,
    draining with an accelerating rate (`-90 → -270`). (`SendSprintBar` in `GameServerListener.Buffs.cs`.)
  - **`e9f24184` StatDamageIIR** — parallel "Spellweaving Cost" gauge (`0x37E68C`, scale `v@33`=`0x0F425C`); the
    accounting twin (`SpellweaveCodec.BuildStatGauge`).
- **DoT when energy empties**: the `Exhausted` debuff (spell `0xB90`, 100s) + the **`0x37E690` "Spellweaving"** gauge
  (scale `0x1B`) drains HEALTH ~`-31705`/tick. Not in the clean dump — see the `..._cancel-weave..._take-dot-when-
  stamina-empty...` capture. The `Exhausted` id is our choice (the client applies it locally; server applies too).
- **Per-add response** (each add): the component's OWN spell applied immediately as a self-buff (necro components take
  effect as you weave — idx0→Arcane Renewal 0x396AE9, etc.) + a begin/complete add-gesture BCC pair (action 186463).
- **Stacking buff**: Spellweaving(1) refreshes to an INCREMENTED base each picker reappear (37E5A8→A9→AA), overlapping
  the previous (retail's ~x2). The energy accelerating-drain rate rises with the stack.
- **CANCEL / deactivate** (2nd `0x205A`): trance-buff end-twins (rank-1 removals of 37F298 + the current 37E5A8 stack
  + 3969D0) + the **trance-EXIT BCC** (action 41302) that takes the char out of the pose + the fire burst.
- **Add-component**: c2s `0x2000` → **`CombatReactiveIIR_c` (`0x54C02089`)**. TWO packets per add:
  - `0x03`-tagged (len 25): `rawPayload = [u32 len][u32 0x54C02089][u32 senderType 0x0000c350][u32 handle][u32 0]
[u8 0x03][u32 componentIndex]` — `componentIndex` at `rawPayload[21:25]` (values **0, 1, 3** for the three
    spells → an index into the caster's weave ELEMENTS, NOT the Spell catalog id; the spell id never appears on the
    wire).
  - `0x02`-tagged (len 21): the ack twin (no index).
- **Fire (deactivate, s2c ~141.6s)**: a `f98e10b3` burst (ids 0x0398/0x039b/0x039a/0x039d) = the accumulated weave
  firing. **Backlash**: chance of ill-effects scaling with weave power (wiki; not yet observed in a dump).
- Noise to ignore: `0x2085` (~2s heartbeat, present in melee dumps too) and the `RightClickItem` potions.

## 5. What's built for spellweaving + what's LEFT

The whole necro flow is built and (except the energy bar) **works in-client**, byte-exact. Architecture:
- `SpellweaveService` (`Faolan/GameServer/Combat/SpellweaveService.cs`) — the FSM + the trance-meter loop. Session
  state on `NetworkClientSession`: `IsSpellweaving`, `WeaveComponents`, `SpellweaveBuffStack`, `Energy`/`EnergyMax`/
  `IsSprinting`/`EnergyLoopActive`. Driven via narrow listener **seams** (ctor args): `ApplySpellweaveBuff` (the
  correct 54-byte buff blob — the shared 50-byte one fails `MagicBuffIIR_c::ReadSubClass`), `SendWorldIir`,
  `SendBuffRemoval` (rank-1 end-twins), `SendSprintBar` (`0x200E`), `EnsureEnergyLoop`.
- `SpellweaveCodec` — reactive list / stat gauge / buff-data / fire-stage builders (byte-exact).
- `BehaviorAnimationCodec` — the trance/component/exit BCC animations (BCC `0x642CD3D6` is the generic behaviour
  channel; shared home with combat, NOT weave-specific).
- `SpellweaveCatalog` — per-class `SpellweaveProfile` (ONLY Necromancer so far) + `EnergyMaxForClass`.
- `GameServerListener.Buffs.cs` — `SendSprintBar` (0x200E) + the persistent energy loop (`RunEnergyMirror`:
  drain-on-sprint / regen-when-idle / step-aside-while-weaving; sprint drain itself is CLIENT-side, we only mirror).

LEFT (in rough priority):

1. **⚠️ Energy bar drops straight to 0 on weave start (in-client bug).** We stream `0x200E` from `session.Energy`
   (full 8910) with an accelerating drain + the `0x37E68C` gauge, energy loop is persistent. Still empties instantly.
   Hypotheses to check with a **frame-by-frame diff of our `0x200E` stream vs the capture**: (a) client energy-MAX
   mismatch so any token reads ~empty; (b) the `0x37E68C` gauge fighting the `0x200E`; (c) `session.Energy` stale-0 in
   some path (e.g. the DoT phase left it 0 and regen hadn't caught up). This is the top thing to fix.
2. **DoT authoritative death** — currently the `0x37E690` gauge only drains the client's health bar visually; wire it
   to real vitals (`EntityVitals` + kill lifecycle) so over-weaving can actually kill.
3. **`componentIndex → stat/effect` table** — the per-component contribution + backlash roll on fire (undecoded; needs
   a targeted dump enumerating a caster's components, or Ghidra).
4. **Trance immobilisation** — have the Move handler reject motion while `IsSpellweaving`.
5. **Other-caster profiles** (Demonologist / Priest of Mitra / Tempest of Set) — need per-class spellweaving captures.
6. **Tune approximations** — sprint drain/regen rates, the energy accelerating-drain curve, the `Exhausted` id, the
   ~8s picker cooldown, and the per-element cooldown (currently the whole picker hides then reappears full, rather
   than per-element zeroing) are all capture-approximate, not pinned.

## 6. Capture dumps (all under `Captures/`, `.tshark.json` are the parsed form)

- Melee: `Hoxxling_mage-heraldofxotli_combos` (HoX), `Hakotep_hox_login_gameplay0` (HoX),
  `AssassinL23_rogue-assassin_login_combos` (Assassin +failed combos).
- Spellweaving: `NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving` (CLEAN ground truth for
  activate/adds/fire), `NecroL80_mage-necromancer_login_spellweave_cancel-weave-and-buff_spellweave-take-dot-when-
  stamina-empty-cancel-weave` (the DoT / Exhausted / energy-to-0 / cancel reference).
- Convert a new pcapng: `Captures/scripts/tshark_dump.sh Captures/<file>.pcapng`.

## 7. Key code files

- `Faolan.Core.Database/Tables/ClassCombo.cs`, `Combo.cs`, `CharacterCombo.cs`
- `Faolan.Core.Database/Repositories/DatabaseRepository.cs` — `GetClassCombos` + `ComboFamilyKey`/`IsRomanNumeral`
- `Faolan.Importer/Captures/CaptureAbilityImporter.cs` — `DeriveClassCombosAsync` + `ClassComboPrefixes`
- `Faolan/GameServer/Combat/ComboActionParser.cs` (parser), `ComboInput.cs` (the `ComboInput` enum), `ComboService.cs`
- `Faolan/GameServer/Combat/SpellweaveService.cs` — the FSM + trance-meter/energy-drain loop
- `Faolan.Core.Network/Codecs/Combat/SpellweaveCodec.cs` — reactive-list / stat-gauge / buff-data / fire-stage builders
- `Faolan.Core.Network/Codecs/Combat/BehaviorAnimationCodec.cs` — trance / component / EXIT BCC animations (0x642CD3D6)
- `Faolan.Core.Network/Constants/SpellweaveCatalog.cs` — per-class `SpellweaveProfile` + `EnergyMaxForClass` (Necro only)
- `Faolan/GameServer/GameServerListener.Buffs.cs` — `SendSprintBar` (0x200E) + the persistent energy loop
- `Faolan/GameServer/GameServerListener.Buffs.cs` — `ApplySpellweaveBuff` (54-byte blob) + `SendBuffRemoval`
- `Faolan.Tests/SpellweaveCodecTests.cs` + `BehaviorAnimationCodecTests.cs` — 12 golden tests · `Captures/scripts/decode_spellweave.py`
- `Faolan/GameServer/GameServerListener.cs` — `_spellweave` ctor seams
- `Faolan/GameServer/GameServerListener.Inbound.cs` — `CombatReactiveIIR` add case + `PerformSpecialAction` (0x205A) handler
- `Faolan.Core.Network/NetworkClientSession.cs` — weave + energy session state
- `Faolan.Core.Network/Constants/LevelComboCatalog.cs` — RETIRED (kept as capture-derived level→combo reference)

## 8. Gotchas

- **Never `pkill -f Faolan.dll`** — it matches the Bash tool's own command line and kills the shell (exit 144, no
  output). Stop the server by PORT: `PID=$(ss -ltnp | grep ':7010' | grep -oP 'pid=\K[0-9]+' | head -1); kill -9
"$PID"`. (Memory: `gotcha_pkill_faolan_dll_self_match`.)
- Kill the server before writing `faolan.db`; don't `git checkout faolan.db` under a running server (makes it
  read-only → stuck at auth).
- After `dotnet ef migrations add`, REBUILD before the server will apply the migration (or run `ef database
update` with the `$PWD/faolan.db` connection override).
