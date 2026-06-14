# Night work — morning review (2026-06-14)

Review guide for the autonomous overnight session. **Branch `development`, committed locally only (not pushed,
`main` untouched). 304 → 322 tests green, build clean.** 12 commits (see `git log 787ec31..HEAD`).

The work split into three buckets: **(A) shipped + byte-exact tested** (no action), **(B) shipped but NEEDS your
in-client pass** (2 items, steps below), **(C) decode/docs/TODO** (no code risk).

---

## A. Shipped + auto-verified (byte-exact golden tests — no action needed)

| What | Commit | Test |
|------|--------|------|
| **Mail model + `DispatchMailMessage` builder** (§9a) — `Mail`/`MailAttachment` tables (migration `AddMail`), `MailMessageCodec` | `53b6486` | `MailMessageCodecTests` (== captured 629 B + 754 B) |
| **AH listings importer** (§9a) — `CaptureListingImporter`, importer `--listings-only` flag; seeded **242 listings** into `faolan.db` | `dec4cf8` | `CaptureListingImporterTests` |
| **Method-name logging** (§4b) — `GameServerMethodNames.Resolve`; GameServer `default` branch logs the method name, not bare hex | `54fae0f` | `GameServerMethodNamesTests` |
| **Remove dead `AdjustSnapshotPrefixRootLength`** (§7) | `a8e5d19` | migrated into `SnapshotBlobLengthTests` |
| **Rename `BigboyTailsFile` → `SnapshotTails`** (§7) | `9c16cd2` | (rename only) |
| **Fold duplicate world-entry-prompt sender** into `WorldFraming.Sender` (§7) | `b59fbf1` | (no behavior change) |

**`faolan.db` change is ONLY the 242 AH listings** (I reverted a full-importer run that would have churned
Items/NpcSpawns from newer captures, and re-seeded just the listings via `--listings-only`). Verify:
`sqlite3 faolan.db "SELECT COUNT(*) FROM TradepostListings"` → 242; Items/NpcSpawns unchanged (1804 / 1849).

---

## B. NEEDS your in-client pass (shipped, byte-exact builders, but only you can confirm the client renders them)

Both are new GM commands. Server from `Faolan/bin/Debug` (`dotnet Faolan.dll`), drive the client, then:

1. **Live item REMOVE — `DispatchRemoveItem` 0x201F** (`adb19d3`, §2). The counterpart to `.additem`.
   - **Test:** `.additem 13817` (an item lands in the bag) → note the slot → `.removeitem <slot>`.
   - **Expect:** the item vanishes from the bag **with no relog**, chat stays connected.
   - If it doesn't vanish: the builder body is byte-exact (`DispatchRemoveItemTests`), so suspect the container/slot
     addressing — captured removes were all container 0x71 (equipped); `.removeitem` uses the backpack (0x68).

2. **On-screen feedback overlay — `FormatFeedbackIIR` 0x8D5A8B6B** (`132d400`, §8). The top-of-screen FIFO banner
   (NOT chat). → `docs/on-screen-feedback.md`.
   - **Test:** `.feedback`.
   - **Expect:** a banner pops at the **top/centre of the screen** (it replays the proven `GenericDisplayAreaName`
     zone-name banner, addressed to your live session).
   - If nothing appears: the body is byte-exact (`FormatFeedbackCodecTests`) and the target instance == the world
     receiver instance (verified on the Strie capture), so the likely culprit would be the world-conversation
     framing/instance for *your* session — grab `AgeOfConan.log`.
   - If it works: next is arbitrary literal text (the FIFO template references a localized RDB string id), then move
     "Resurrection point discovered" etc. off chat onto these banners.

*(After client runs: `git checkout -- faolan.db` to drop any session writes.)*

---

## C. Decode / docs / TODO (no runtime risk)

- **On-screen feedback fully decoded** → `docs/on-screen-feedback.md`, memory `protocol_onscreen_feedback`
  (`FormatFeedbackIIR` grammar + the `showfifomessagefromserver` FIFO token family).
- **New TODO items added** (your call on priority):
  - §2 — **Directional combat + defensive shields** ("Real Combat": directional swings + the target's dynamic
    shield distribution; three pure-decode steps from the combat captures). `9865386`.
  - §10 — **AoC-specific systems not yet started** (combos, fatalities, spellweaving, mounted combat; the
    recovered-but-unbuilt opcodes — offline training / apprentice / ranger tracking / vanity / minigames; and the
    big world systems — sieges/guild cities, tradeskills, Tortage day/night phasing, AA). `839fd2f`.
  - §7 — **Codec-consolidation candidates** (build-only builders → `Parse`/`Build` codecs; honestly noted as a
    consistency refactor since most already have golden build tests — the combat-packet extraction from
    `CombatService` is the one with real value). `021c746`.

---

## If anything looks wrong
- Everything is local commits on `development`; `git reset --hard 787ec31` rewinds the whole session.
- Each feature is its own commit (table above) — revert individually if needed.
- The Night log in `docs/autonomous-session-handoff.md` has the per-milestone trail.
