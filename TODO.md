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
**Expected:** the health/stamina/mana bars actually move. If nothing happens, grab the client log.

## 2. Mounts  (commit: combat loop / mount summon)
Real mount spell decoded as `0x3BAD7D` (Fading Black Horse summon).
- [does nothing] `.mount` — should trigger the mount summon cast (and ideally put you on the horse).
- [same] If nothing mounts, try `.mount 3BAD7D` explicitly, and tell me what (if anything) happens
      (cast bar? horse spawns? you ride?). Full mounting may still need the mount-entity spawn — your
      observation tells me what's missing.

## 3. Combat (cast → damage → vitals)  (commit: combat loop)
- [Character has no spells / attacks] Cast a damaging spell at an NPC/mob. The target's health bar should drop, and on 0 HP the server
      logs a kill (despawn/XP not implemented yet).
**Expected:** target HP visibly decreases. Damage is a provisional formula for now.

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

## 6. Conversations — right NPC, right dialog  (commit: key conversations by NPC handle)
On a **beach login** (existing character), not the create flow:
- [first dialog is kalanthes, sfter selecting first option it switches to second dialog of casilda, advancing to third step locks the chat (unable to exit with esc)] Talk to **Casilda** — she should give HER dialogue, not Kalanthes's regular dialog.
- [ Dialos opens normally, and camera focuses on him. But no dialog lines to select, can exit conversation with esc] Talk to **Kalanthes** — his dialogue.
**Known limit:** in the *create-world* flow specifically, Casilda may still fall back to the scripted intro —
her create-world conversation isn't in the captures the analyzer can extract yet (needs a fresh capture).

## 7. Create-world Kalanthes intro  (commit: answer create-world spawn polls per-entity) ⭐ highest priority
The auto-cutscene where you wash ashore and Kalanthes walks up + starts the chat.
- [No, still as bugged as before. after char create the game is in a not normal state. relog is required] Create a NEW character → enter world → does Kalanthes walk up and the conversation auto-start?
- [ ] **Grab `AgeOfConan.log` either way.** Specifically check whether
      `CinematicCamera_c::InitPath tried to set an invalid camera path "Kalanthes"` is GONE.
      - Error gone + Kalanthes works → fixed.
      - Error gone but still no walk-up → entities now confirmed, something else in the cinematic; send the log.
      - Error still present → spawn confirmation still off; send the log.

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
