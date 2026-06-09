# In-client test checklist

Things changed this session that I couldn't verify (no client on my side). Work through these in-client and
note what happens. For anything that fails, the **client log** is the most useful thing to grab:
`/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log`
(look for `stream bad`, `Dispatcher call ... failed`, `invalid camera path`, `ResourceManager ... does not exist`).

Reminder: you revert `faolan.db` between tests. After pulling these changes, the DB needs the new schema —
either keep the committed `faolan.db` (it already has the migrations + reimported data) or run
`dotnet run --project Faolan.Importer` once. New tables this session: `CharacterMaxStats`, plus the
conversation key change.

---

I PUT THE LOGS IN ./logs-test

---

## 1. Vitals packet `0x96B8DC59`  (commit: combat loop)
The decoded vitals/HP packet, wired to GM commands.
- [works] `.hurt 200` — your health bar should drop by ~200.
- [work up to 247, even though max says 3668] `.heal 200` — health bar should rise (clamped at max).
- [ ] `.vitals` (no args) — should set the bar to your character's MaxStats (class/level defaults).
- [ ] `.vitals 1000 800 500` — sets health/stamina/mana to those values.
- [ ] `.setstat 36 25` — sets Level to 25 on the sheet (statId 0x36 = Level).
**Expected:** the health/stamina/mana bars actually move. If nothing happens, grab the client log.bbbbbbb

## 2. Mounts  (commit: combat loop / mount summon)
Real mount spell decoded as `0x3BAD7D` (Fading Black Horse summon).
- [does nothing, shows nothing] `.mount` — should trigger the mount summon cast (and ideally put you on the horse).
- [same] If nothing mounts, try `.mount 3BAD7D` explicitly, and tell me what (if anything) happens
      (cast bar? horse spawns? you ride?). Full mounting may still need the mount-entity spawn — your
      observation tells me what's missing.

## 3. Combat (cast → damage → vitals)  (commit: grant starting abilities on login)
### FIX 2026-06-09: the player is now GRANTED their starting attacks on login.
On login the server sends the character's ClassAbilities (the universal melee attacks 769094 "Normal Attack
Left" / 769099 "Normal Attack Front") via the retail 0x2052 ability-grant packet, rebuilt DB-driven from the
Abilities rows (identity quad + the new `Slot` word). Byte-for-byte verified against the strickenbla combat
capture (AbilityGrantTests). They ride the spawn conversation, sent right after the world-burst replay.
- [ ] After a beach login, the two basic attacks should appear (drag to the hotbar from the abilities/feats
      window if not auto-placed).
- [ ] Target an NPC/mob and use an attack. The target's health bar should drop, and on 0 HP the server logs a
      kill (despawn/XP not implemented yet).
**Expected:** target HP visibly decreases. Damage is a provisional formula for now. If NO abilities show up at
all, grab the client log — the grant may need to fire at a different point in the world-entry sequence.

**Follow-up (not done):** only the universal melee *abilities* are granted (every class gets 769094/769099) —
enough to exercise cast→damage→vitals. Actual *spells* (e.g. class 29's spell 165241 in ClassAbilities.Spells)
use a SEPARATE grant packet that isn't decoded/wired yet. The C++ archive's addSpell (opcode 0x09, just the
spell id) is the OLD scheme and almost certainly dead like the old 0x4d ability grant was — needs the retail
spell-grant packet found in a capture before wiring.

## 4. Teleport GM command  (commit: fix teleport)
Rewritten to reuse the world-entry render path.
- [Does move player & apply buff, but maps does not change] `.teleport 2010` (also try `2040`, `3000`) — these maps have a stored world burst AND a real spawn
      position, so they're the best targets. You should load into the new map with NPCs.
- [same] `.teleport 4010` (Tortage Beach) should also work.
**Expected:** map changes and renders. Note: retail teleport reconnects to a new world server; ours does a
soft map-change on the same connection, so it may differ.

## 5. Character vitals from DB  (commit: CharacterMaxStats)
- [ ] Create a character, pick a class, finish naming, then check the character sheet vitals in-world —
      Health/Mana/Stamina should reflect that class (e.g. a mage has more mana, a soldier more health),
      not the level-1 rogue default.

## 6. Conversations — right NPC, right dialog  (commit: route conversations by NPC name)
### FIX 2026-06-09: root cause = session-volatile NPC handles; now routed by NPC NAME.
The beach burst spawns Kalanthes=530599/Casilda=530694, but the conversation nodes were captured in a different
session (handles 319114/319209) — so the old handle key never matched and the fallback served the wrong NPC.
Now: ConversationNode carries NpcName (resolved at import from the capture's spawn); the burst replay records live
handle→name; clicks route by name → right NPC or nothing, never wrong. RE-TEST a beach login:
- Casilda should give HER dialog (or nothing if her quest-mode isn't captured), NOT Kalanthes's.
- Kalanthes should give his own lines.
(Coverage caveat: if the live player's quest mode for an NPC isn't in the captures, no dialog shows — that's a
data gap, not mis-routing now.)

On a **beach login** (existing character), not the create flow:
- [first dialog is kalanthes, sfter selecting first option it switches to second dialog of casilda, advancing to third step locks the chat (unable to exit with esc)] Talk to **Casilda** — she should give HER dialogue, not Kalanthes's regular dialog.
- [ Dialos opens normally, and camera focuses on him. But no dialog lines to select, can exit conversation with esc] Talk to **Kalanthes** — his dialogue.
**Known limit:** in the *create-world* flow specifically, Casilda may still fall back to the scripted intro —
her create-world conversation isn't in the captures the analyzer can extract yet (needs a fresh capture).

## 7. Create-world Kalanthes intro  (commit: answer create-world spawn polls per-entity) ⭐ highest priority
The auto-cutscene where you wash ashore and Kalanthes walks up + starts the chat.

### PINNED 2026-06-09 (logs-test/AgeOfConan.log + logs.txt + screenshot)
**The intro now TRIGGERS — real progress.** Server sends MapChange→4000 (Slave Galley) + 16 SpawnObject +
6 FA015807 entity spawns + answers the spawn-polls with SpawnChecks. Client receives the `PlayOrSkip` then
`ThisIsKalanthes` scry messages and *attempts* the full drowning→Kalanthes cinematic. So the world-build and
script-trigger sequence is essentially working.

### DECODED 2026-06-09: the retail ScryScript cutscene sequence (incl. the resource manifest)
The cutscene is driven by `ScryScriptIIR` (IIR sub-opcode **0xA36D3B74**, protobuf `ProtoScryScriptIIR`), now
fully decodable — run `Captures/decode_scryscript.py` on the dikketest create capture. The retail server->client
sequence is:
1. **PreloadAssets** — token `playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)`,
   **+ a big RDB resource manifest** in string-param-2: `1010004:793713; 1070003:4000; 1000097:4000; 1010030:156211;
   1000059:4000; 1010300:4000; 1000049:4000; 1066611:4000; 1010506:4000; 1000058:4000; ...` (the Slave Galley 4000
   playfield + cutscene assets the client must load).
2. **ScriptMessage "PlayOrSkip"** → `StartScriptGroupOnClient(35820)` → `ScriptMessage NicknameOk/<name>` →
   `ScriptMessage CharCreationFinished` → `StartScriptGroupOnClient(48126)`.
3. **PlayOrSkipQuestion** — token `...character_creation Tortage Beach (server-side)` + the Beach (4010) resource
   manifest (`1010003:3147889; ...`) → `StartScriptGroupOnClient(385910)` → **ScriptMessage `ThisIsKalanthes`** →
   `StartScriptGroupOnClient` → `ScriptMessage WelcomeTutorial`.
**Checked against our server (2026-06-09): we ALREADY send the Slave Galley `PreloadAssets` with the full manifest
verbatim** (`RetailCreateEditorCreationScriptBurst.cs:94-117`, the `playorskip(72)` packet + the entire
`1010004:793713;...` resource list) followed by `PlayOrSkip`. So a missing/empty `PreloadAssets` is NOT the gap.
Moreover the failing ids in the client log (`1000519:4000`, `1000103:4000`, `1066604:4010`) are **not in the
manifest at all** — they are `class Playfield requested CHILD resource` lookups, i.e. resources the playfield pulls
from the client's LOCAL RDB, not server-sent. **This reinforces the existing client-side RDB / project-config
diagnosis below** (the server is feeding the right script + manifest; the client can't resolve the playfield's own
child resources).
**Still worth checking (the one server-side unknown):** the SECOND scry stage — the Tortage Beach (4010)
`PlayOrSkipQuestion` with its own manifest (`1010003:3147889;...`), then `StartScriptGroupOnClient(385910)` →
`ThisIsKalanthes` → `WelcomeTutorial` — arrives LATER in retail (decode #7-#12), after the beach map-change. Our
create-world flow is chunked per client trigger; confirm that beach scry chunk (and its manifest) is actually
replayed and not truncated at the `ApproachCasilda` finale. Use `Captures/decode_scryscript.py` to compare.

**It breaks on MISSING CLIENT PLAYFIELD RESOURCES, not a server packet defect.** Client log:
- `ResourceManager - class Playfield requested child resource 1000519:4000 / 1000103:4000 / 1000519:4010 /
  1066604:4010 does not exist in any RDB sources`
- `GameData.Playfield - PlayfieldStaticPoints_c::ReadBlob: Error reading the static ai points`
- → EVERY cinematic camera path is invalid (`CC_Drowning_Above/TiltDown/TrackUpFromBeneath`, `Awake`,
  `Kalanthes`, `GetUp`) → camera stuck → the grey, untextured scene in the screenshot.
- `ThisIsKalanthes` carries object `123456789:123456789` = the client's "unresolved object" sentinel (the
  CLIENT-SIDE intro script couldn't bind Kalanthes) — consistent with the scene/AI data not loading.

These `... does not exist in any RDB sources` lookups are the client's LOCAL RDB; the server doesn't serve RDB
resources. **Retail plays this cutscene on this same client, and the 27 GB rdbdata is intact (`Found 974.5 MB
Unused space in all rdbdata files`) — so the data is NOT absent.** Corrected read (2026-06-09):
- NOT a server packet defect, NOT data-absence. The leading suspect is the client-side
  `ERROR: ResourceManager - No project defined, ... running dreamworld control center and selecting a project
  should fix it` (log line 3). The client recovers enough to load base geometry/names, but **project-scoped**
  resources — exactly the intro cinematic camera paths + static-AI-points — don't resolve, so EVERY camera path
  fails uniformly. Geometry loads; cinematic doesn't. That's a client project-config issue, not our server.
- Our `le.idx` (importer index) is a CURATED SUBSET (195 types, lacks playfield type 51100 entirely) — it CANNOT
  confirm/deny these resources. Definitive check needs parsing the client's real `RDBHashIndex.bin` (RDHI v6).
- NEXT: (a) CLIENT — check/repair the Dreamworld project config (the `No project defined` error); cheap, possibly
  decisive. (b) TOOLING — parse `RDBHashIndex.bin` to confirm the cinematic resources for pf 4000/4010 exist (we
  have the dumps), proving the data's present and the issue is resolution/config, not absence.

### AWAITING: retail client log (user will capture at home) — DIFF against logs-test/AgeOfConan.log
The decisive test. Capture an `AgeOfConan.log` from a retail/working run of the SAME create-world intro, then
compare line-for-line with ours. What each outcome tells us:
1. **`ResourceManager - No project defined`** — present in retail too? → benign startup noise, NOT the cause.
   Absent in retail? → our client's project config is the culprit (client-side, not our server).
2. **`class Playfield requested child resource 1000519:4000 ... does not exist`** + static-AI-point errors —
   present in retail too (yet cutscene works)? → benign; real failure is elsewhere. Absent in retail? → retail
   resolves those resources; ours doesn't (config/resolution, or we put the client in a different playfield state).
3. **`CinematicCamera_c::InitPath ... invalid camera path "CC_Drowning_*/Awake/Kalanthes/GetUp"`** — gone in
   retail? → confirms the camera paths DO resolve normally; our failure is downstream of the missing pf resources.
4. **`ThisIsKalanthes` scry msg** — retail's `msgobject.name` should be a REAL dynel id, not our `123456789:
   123456789` sentinel. Confirms whether the Kalanthes binding (server-supplied handle) differs.
5. Bonus: compare the `Playfield.Loading - Loading stats for Pf Proxy` lines (Model/RunningPlayfield/Subgroup)
   to see if retail loads pf 4000/4010 with different Subgroup/instance than ours.
Put the retail log in `logs-test/` (e.g. `AgeOfConan-retail.log`) so it sits next to ours for the diff.

**Secondary server-side cleanups (won't alone fix the cutscene, but worth doing):**
- `rpc - ... OverrideGameTweaks() ... stream bad after 'overrides'!` → `Dispatcher call ... (OverrideGameTweaks)
  failed. Disconnecting ( Route ... => PlayerInterface)`. Known overrides bug, but here it DISCONNECTS a route
  mid-create-world. Confirm it isn't severing anything the intro needs (the intro did proceed, so likely tolerated).
- `GameCode.Chars - GameCodeDynelFullUpdateIIR::Activate Ignoring IIR for already existing Dynel 50000 <inst>`
  (341446/341448/341468/341469) — server RE-sends beach entities the client already has; client ignores the dupes.
  Indicates a double-spawn (initial create-world entry + world-burst replay). Not fatal; de-dupe it.

## 8. ⚠️ Float endianness on raw-body packets  (commit: float endianness)
I changed `MapChange`, `ReadyForPlayScreen`, the Move echo, and the `/setpos`-style GM packet to write raw
position floats **big-endian** (they were wrongly little-endian). This touches the **login path**, so it's the
most likely to regress something.
- [Works] Normal login — do you still spawn at the correct position/orientation (not at 0,0,0 or somewhere weird)?
- [Works] Movement still works normally?
**If a character spawns in the wrong place or login breaks, this change is the first suspect.**

## 9. Map rendering after instance-detection fix  (commit: fix instance detection)
- [The out of range ones need to be supported as well i assume, if they are in the dumps that must mean they exist. Wrong parse?] Maps that have a world burst (2000, 2010, 2040, 3000, 4000, 4010) should render on login/teleport.
      Maps 1000 and 11040 were intentionally dropped (their captured instance was out of range) — they'll
      fall back to the no-NPC hand-built path.

---

## Notes / known non-blockers (no action needed unless curious)
- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` every session and drops that
  one route; harmless, pre-existing.
- Analyzer tail-drop on ~3 GS streams (Strie-create/login, faolanx-charcreation) — a few KB short vs ground
  truth; doesn't affect gameplay, logged in README.
