# Handoff — Combos & Spellweaving

Detailed handoff for the AoC combo (melee) + spellweaving (caster) systems. Companion to `docs/combos.md`
(the design overview). This file is the "where we are / how to continue" for a fresh session.

## 0. Status at a glance

| Piece | State |
|---|---|
| `ClassCombo` table + migration + enum CHECK | ✅ committed |
| `ClassCombo` filled (8 combo classes, 292 rows, casters empty) | ✅ committed |
| Rank-superseding of the combo grant (one per family) | ✅ committed |
| `ComboActionParser` + `ComboService` (data side) | ✅ committed |
| Grant wired to `ClassCombo` (fixed necro learning fighter combos) | ✅ committed |
| **Spellweave wire decode** (activate/add/deactivate + buffs/meter/fire) | ✅ DECODED |
| Spellweave FSM (state machine, toggle + add detected in-client) | ✅ committed (skeleton) |
| Spellweave full activate RESPONSE (buffs+meter+reactive), per-add, fire | ❌ TODO (the real build) |
| **Melee wire decode** (cid → combo, animByte → direction) | ⚠️ BLOCKED on client Ghidra RE |
| Melee execution FSM | ❌ TODO (blocked by the decode) |
| ⚠️ Regression: necro pets from a prior session went "permanent" | ❌ needs investigation (likely my importer run) |

Commits this session (newest first): `5f1efb2a` spellweave FSM · `c2e87884`/`49662677`/`f02a8861` docs ·
`50a4b4bb` ComboService · `02ffec0c` ComboActionParser · `753e5281`/`53dcce4e` fill ClassCombo(8 classes) ·
`6bd680a9` superseding · `0424a594`/`45d1291c` derive ClassCombo(HoX) · `1d86a4c7` ClassCombo table + grant.

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
  (Soldier_/Rogue_ archetype-shared, feat-trees Reaver/Liberator/Lich/…, Slave_ starters, combo_npc, racials) are
  deliberately EXCLUDED.
- **Grant** — `IDatabaseRepository.GetClassCombos(level, class)` returns the combos RANK-SUPERSEDED by family
  (`ComboFamilyKey` in `DatabaseRepository.cs`, the combo twin of `GetStartingBarSpells`): one per line at the
  highest qualifying rank (lvl-24 HoX → Hell Strikes II not I+II, matching the `0x2076` book). Emitted by
  `GrantCharacterCombos` (`GrantAbilitiesBuilder.cs`) + the two level-up ding paths (CombatService,
  FormatFeedbackSender). This REPLACED the class-independent `LevelComboCatalog` (retired to a data reference) —
  which was the bug where a Necromancer re-"learned" Sweep/Slam/Hell Strikes every login.
- **`ComboActionParser`** (`Faolan/GameServer/Combat/ComboInput.cs`) — normalises all 33 `Actions` tokens into the
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

- **Activate / deactivate = a TOGGLE**: c2s opcode **`0x205A`** (`GameServerOpcodes.PerformSpecialAction`), body
  `[u32 0x000000FC]`. SAME value both presses (like the mount toggle) — first opens the trance, second fires+closes.
- **Activate RESPONSE (s2c, ~103.8s)** — the currently-MISSING part that stalls our client at "started":
  - **3× trance buffs**: `ApplySpell` (`0x6B87E316`), wire ids **`0x37F29800`, `0x37E5A800`, `0x3969D000`** (the
    `(base<<8)|rank` form; base 0x37F298 etc., rank 0x00). ApplySpell body =
    `[len][6b87e316][EntityClass 0x0000c350][applyTo][u16 0][u32 spellId][EntityClass][applyFrom][data tail]`;
    duration lives at `data+8` in centiseconds (see `ApplySpell` in `PostReadyFollowUp.cs` + `ActiveBuff`). First
    buff verbatim: `000000506b87e3160000c3500802e5d4000037f298000000c3500802e5d40000…`.
  - a **`CombatReactiveIIR`** (`0x54C02089`) s2c ack, a `0x204e` effect, and
  - the **`e9f24184` trance meter** (from ~105.4s): s2c, a u32 at body offset 16 that DRAINS (~-90/tick) — the
    trance gauge. Body: `00000051 e9f24184 0000c350 0802e5d4 000022ce…`.
- **Add-component**: c2s `0x2000` → **`CombatReactiveIIR_c` (`0x54C02089`)**. TWO packets per add:
  - `0x03`-tagged (len 25): `rawPayload = [u32 len][u32 0x54C02089][u32 senderType 0x0000c350][u32 handle][u32 0]
    [u8 0x03][u32 componentIndex]` — `componentIndex` at `rawPayload[21:25]` (values **0, 1, 3** for the three
    spells → an index into the caster's weave ELEMENTS, NOT the Spell catalog id; the spell id never appears on the
    wire).
  - `0x02`-tagged (len 21): the ack twin (no index).
- **Fire (deactivate, s2c ~141.6s)**: a `f98e10b3` burst (ids 0x0398/0x039b/0x039a/0x039d) = the accumulated weave
  firing. **Backlash**: chance of ill-effects scaling with weave power (wiki; not yet observed in a dump).
- Noise to ignore: `0x2085` (~2s heartbeat, present in melee dumps too) and the `RightClickItem` potions.

## 5. What's built for spellweaving (skeleton) + what's LEFT

Built (`5f1efb2a`): `SpellweaveService` (`Faolan/GameServer/Combat/SpellweaveService.cs`) — the state machine.
Session state on `NetworkClientSession`: `IsSpellweaving` + `WeaveComponents`. Wiring: the `0x205A`
`PerformSpecialAction` handler (`GameServerListener.cs`) calls `ToggleAsync`; `Handle0x2000` routes
`CombatReactiveIIR` (added to the c2s `GameServer0X2000Opcodes` enum) to `AddComponentAsync`. **Confirmed
in-client**: activating a weave logs `Spellweave START for 'NecroL80'` — so the toggle path is LIVE.

LEFT to make it actually work in-client (the real, byte-exact build; the client STALLS at "started" without the
full activate response, because it never enters the interactive trance that lets it send the adds):
1. **Send the activate RESPONSE on START** — the 3 trance buffs (`ApplySpell` 0x37F29800/0x37E5A800/0x3969D000),
   the `CombatReactiveIIR` ack, and the `e9f24184` meter. This is what unblocks the adds. (Have `ToggleAsync`
   return Started/Fired so the listener can emit the buffs on Started via the existing `ApplySpell` helper.)
2. **Per-add response** on each `CombatReactiveIIR` add (what retail sends back per add — decode from the dump).
3. **Fire on deactivate** — build the `f98e10b3` burst from the accumulated components + roll backlash.
4. **`componentIndex → stat/effect` table** — the actual per-component contribution (needs the weave-element
   table: a targeted dump enumerating a caster's components, or Ghidra).
5. **Trance immobilisation** — have the Move handler reject motion while `IsSpellweaving`.
6. Do it with **byte-exact golden tests** (the project's acceptance gate), not live-patching.

## 6. Capture dumps (all under `Captures/`, `.tshark.json` are the parsed form)

- Melee: `Hoxxling_mage-heraldofxotli_combos` (HoX), `Hakotep_hox_login_gameplay0` (HoX),
  `AssassinL23_rogue-assassin_login_combos` (Assassin +failed combos).
- Spellweaving: `NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving` (CLEAN ground truth),
  `NecroL80_mage-necromancer_login_spellweaving`.
- Convert a new pcapng: `Captures/tshark_dump.sh Captures/<file>.pcapng`.

## 7. Key code files

- `Faolan.Core.Database/Tables/ClassCombo.cs`, `Combo.cs`, `CharacterCombo.cs`
- `Faolan.Core.Database/DatabaseRepository.cs` — `GetClassCombos` + `ComboFamilyKey`/`IsRomanNumeral`
- `Faolan.Importer/Captures/CaptureAbilityImporter.cs` — `DeriveClassCombosAsync` + `ClassComboPrefixes`
- `Faolan/GameServer/Combat/ComboInput.cs` (parser), `ComboService.cs`, `SpellweaveService.cs`
- `Faolan/GameServer/Outbound/GrantAbilitiesBuilder.cs` — `GrantCharacterCombos`
- `Faolan/GameServer/GameServerListener.cs` — `_spellweave`, `PerformSpecialAction` handler
- `Faolan/GameServer/Inbound/Handle0x2000.cs` — `CombatReactiveIIR` add case
- `Faolan.Core.Network/NetworkClientSession.cs` — `IsSpellweaving` + `WeaveComponents`
- `Faolan.Core.Network/Packets/Opcodes.cs` — `CombatReactiveIIR` (c2s) + `PerformSpecialAction` (0x205A)
- `Faolan/GameServer/Combat/LevelComboCatalog.cs` — RETIRED (kept as capture-derived level→combo reference)

## 8. ⚠️ Open regression — necro pets went "permanent"

User reported that a necro's pets from a PRIOR session are now permanent. Prime suspect: the full importer run in
`0424a594` (`dotnet run --project Faolan.Importer -- --skip characters`) over the Hakotep dumps folded
per-session spawns into `faolan.db` as permanent `NpcSpawns` (the known "captures carry per-session bloat" risk —
see the `npc_data_retirement_blocked_by_capture_bloat` note). NpcSpawns went 910 → 1049 that run. To check/back
out: compare `NpcSpawns` against the pre-`0424a594` DB (`git show 0424a594~1:faolan.db`), and delete any
pet-creature spawns that leaked in (necro pets = Arcanist/Magus/etc. per the pet subsystem `#307` note). NB
`NpcSpawns` has no `TemplateId` column — check its actual schema before querying.

## 9. Gotchas

- **Never `pkill -f Faolan.dll`** — it matches the Bash tool's own command line and kills the shell (exit 144, no
  output). Stop the server by PORT: `PID=$(ss -ltnp | grep ':7010' | grep -oP 'pid=\K[0-9]+' | head -1); kill -9
  "$PID"`. (Memory: `gotcha_pkill_faolan_dll_self_match`.)
- Kill the server before writing `faolan.db`; don't `git checkout faolan.db` under a running server (makes it
  read-only → stuck at auth).
- After `dotnet ef migrations add`, REBUILD before the server will apply the migration (or run `ef database
  update` with the `$PWD/faolan.db` connection override).
