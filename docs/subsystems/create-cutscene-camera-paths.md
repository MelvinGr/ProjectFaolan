# Create-flow intro cutscenes — camera-path / playfield mechanism (Ghidra, 2026-06-15)

> Related: `docs/subsystems/create-editor-bursts.md` decodes the create-editor `0x2000` IIR stream itself (framing +
> record grammars); this doc covers the cutscene/camera-path mechanism layered on top of it. Both feed #189.

The post-create intro is **two separate cutscenes on two separate playfields**, each on its own
GameServer connection. This is confirmed across four create captures (editor/world MapChange map ids):

| Cutscene | Connection | Playfield (MapChange `0x5D85BFC7`) | Arming packet |
|---|---|---|---|
| **Drowning** (slave galley sinks, player knocked out underwater) | editor | **4000** `0x0FA0` "Slave Galley" | `playorskip(72)\|…\|NEW_character_creation Slave Galley` |
| **Kalanthes** (wash up on beach, Kalanthes walks up + kneels, ApproachCasilda) | world | **4010** `0x0FAA` "Tortage Beach" | `playorskip(100)\|…\|character_creation Tortage Beach` |

Captures: hoxxling editor=:7018→4000 / world=:7012→4010; stricken998 create-no-worldenter editor :7030→4000;
randochar :7016→4000 / :7015→4010; strickenen :7012→4000 / :7013→4010.

## Camera paths are PLAYFIELD-SCOPED (the "invalid camera path" root cause)

From `AgeOfConan.exe` (Ghidra, project `Captures/ghidra/output`, scripts in `Captures/ghidra/`):

- **`CameraPathMap_c::LoadPlayfieldPaths`** (`FUN_00515420`) loads an RDB resource of **type `0xF426F`
  = 1000047** (`e_RDB_CameraPaths`) keyed by the **playfield id** (its `param_2`). On failure it logs
  `CameraPathMap_c::LoadPlayfieldPaths: Cant load camerapaths [e_RDB_CameraPaths:<id>]`.
- **`CinematicCamera_c::InitPath`** (`FUN_005230b0`) emits `…tried to set an invalid camera path "<name>"`
  when the named path is not in the **currently-loaded playfield's** camera-path set.
- `CC_Drowning_Above` (and the other `CC_Drowning_*` steps) are referenced from
  `CameraPathScriptCommand.cpp` (`FUN_013b09b0`).

**Therefore:** the drowning cutscene's `CC_Drowning_*` paths live in **playfield 4000's** camera-path
resource. They only resolve while the client is in playfield 4000. The historical
`invalid camera path "CC_Drowning_*"` / `"Kalanthes"` errors were the client being on the **wrong
playfield** when `InitPath` ran — not a data/animation bug.

## Our flow already satisfies this

- `SendRetailCreateEditorInitialBurst` (`CreateFlowService.cs`) sends MapChange → **4000**
  (`0x0FA0`) on the editor connection — CONSTRUCTED via `CreateEditorRecordBuilder.BuildPlayfieldFiir` from
  `StartingData.EditorMapId` (= 4000), not a verbatim blob — and `SendRetailCreateEditorCreationScriptBurst`
  sends the `NEW_character_creation Slave Galley` playorskip on that same connection. So the editor IS on
  playfield 4000 with the drowning cutscene armed.
- The drowning cutscene is **client-autonomous**: in every capture the server sends NO packets between the
  `playorskip` and the client's `IntroDone` — the client plays the whole drowning sequence itself. Our
  verbatim editor burst (from `create-no-worldenter`) is byte-identical to the cutscene-played captures for
  this stretch, so the delivered data is complete.

This matches the last in-client result (camera + audio correct; no more `invalid camera path`). **The
camera-path/playfield theory is resolved and is no longer the blocker.**

## Remaining gap: actor animation (client-script / content, not server packets)

What still doesn't play (per the **last known** in-client create run — see caveat below) is the scripted
**actor animation** (galley sinking, the player avatar's knockout). Because the drowning cutscene is
client-autonomous, there is no server directive to add — the client-side galley script (RDB script id
105937) spawns and drives its own actors. Lead:

- **Cinematic actor-tagging.** `TagCharacterForCinematicScriptCommand` (`FUN_014514c0`) parses an
  `"object"` reference identifying which character the cinematic controls. This is the server-driven path
  used by the **Kalanthes** beach cutscene (`ThisIsKalanthes`/`Kneel`), not the autonomous drowning one.

> **Caveat — no recent create run.** The latest `AgeOfConan.log` (2026-06-14 22:51) is a **failed login**
> (`Authentication Failure (14)` 4 s after boot), not a create-flow run, so it does NOT show the current
> cutscene state. Its `1010004:<id> does not exist` lines are **boot-time texture loads**, unrelated to the
> cutscene manifest — do not treat them as evidence of a content gap. The actor-animation conclusion above
> comes from earlier in-client sessions (memory `createworld_cutscene_diagnosis`); re-confirm it with a fresh
> create run before acting on it.

## Pinned next step (needs the live client)

First, **do a fresh create run** and grab `AgeOfConan.log` — we don't currently have a recent one, so the
actual current symptom is unconfirmed. Then capture **our** server's create flow and diff the **editor**
stream packet-by-packet against the retail editor stream (stricken998 `create-no-worldenter` :7030) for any
runtime divergence (timing of the deadlock-breaker post-ready flush vs. the playfield-4000 load; a missing
prerequisite spawn). The static data says our packets are correct, so the remaining isolation is a live-run
diff.

Reproduce the RE: `Captures/ghidra/DumpCutscene.java` (strings + xrefs), `DecompileFns.java`
(`FNS=00515420,005230b0,014514c0`), `FindStr.java` (`NEEDLE=…`) against `Captures/ghidra/output` (see
`Captures/ghidra/README.md`).
