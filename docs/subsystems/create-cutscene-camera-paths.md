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

From `AgeOfConan.exe` (Ghidra, project `tools/ghidra/output`, scripts in `tools/ghidra/`):

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
  (`0x0FA0`) on the editor connection — CONSTRUCTED via `PlayfieldFCodec` from
  `StartingData.EditorMapId` (= 4000), not a verbatim blob — and `SendRetailCreateEditorCreationScriptBurst`
  sends the `NEW_character_creation Slave Galley` playorskip on that same connection. So the editor IS on
  playfield 4000 with the drowning cutscene armed.
- The drowning cutscene is **client-autonomous**: in every capture the server sends NO packets between the
  `playorskip` and the client's `IntroDone` — the client plays the whole drowning sequence itself. Our
  verbatim editor burst (from `create-no-worldenter`) is byte-identical to the cutscene-played captures for
  this stretch, so the delivered data is complete.

This matches the last in-client result (camera + audio correct; no more `invalid camera path`). **The
camera-path/playfield theory is resolved and is no longer the blocker.**

## Actor animation is client-side content, not server packets

The scripted **actor animation** (galley sinking, the player avatar's knockout) is client-side. Because the
drowning cutscene is client-autonomous, there is no server directive to add — the client-side galley script
(RDB script id 105937) spawns and drives its own actors.

- **Cinematic actor-tagging.** `TagCharacterForCinematicScriptCommand` (`FUN_014514c0`) parses an
  `"object"` reference identifying which character the cinematic controls. This is the server-driven path
  used by the **Kalanthes** beach cutscene (`ThisIsKalanthes`/`Kneel`), not the autonomous drowning one.

> **Caveat — evidence provenance.** The actor-animation conclusion above comes from earlier in-client
> sessions (memory `createworld_cutscene_diagnosis`), not the latest `AgeOfConan.log` (2026-06-14 22:51),
> which is a **failed login** (`Authentication Failure (14)` 4 s after boot), not a create-flow run. Its
> `1010004:<id> does not exist` lines are **boot-time texture loads**, unrelated to the cutscene manifest —
> not evidence of a content gap.

Reproduce the RE: `tools/ghidra/DumpCutscene.java` (strings + xrefs), `DecompileFns.java`
(`FNS=00515420,005230b0,014514c0`), `FindStr.java` (`NEEDLE=…`) against `tools/ghidra/output` (see
`tools/ghidra/README.md`).

## An IN-WORLD cutscene: the achievement VISTA (#740)

The create-flow intro is not the only cutscene. Interacting with the right world object plays a camera fly-through
and awards an achievement, and the whole trigger chain is captured in one 50-second window
[V2: NecroL80_mage-necromancer_login_gameplay_annotated_2.tshark.json frames 4024-4299]:

| t | direction | record | what |
|---|---|---|---|
| 17:41:03.9 | c2s | `BccIIR` `UseDynel` (0x12) | the player interacts with the object |
| 17:41:04.0 | s2c | `BccIIR` ack | |
| 17:41:04.2 | s2c | `SimpleDynelVisualChangeIIR` `object_opened` | the lid opens |
| 17:41:04.2 | s2c | `BccIIR` `ObjectInteraction` + a `remoteformat` | ⭐ names the mechanism, below |
| 17:41:09.2 | s2c | `MagicBuffIIR` spell `0x4C45` | a buff for the duration of the scene |
| 17:41:09.2 | s2c | **`GameActionListIIR` type 53260** | ⭐ the cutscene itself, below |
| 17:41:09.2 | c2s | `CinematicTransition` `…01` | the client says it ENTERED the cinematic |
| 17:41:10.9 | s2c | `SimpleDynelVisualChangeIIR` `object_closed` | the lid shuts |
| *…41 s of fly-through…* | | | |
| 17:41:50.3 | c2s | `CinematicTransition` `…00` | the client says it LEFT |
| 17:41:53.1 | s2c | `ScryScriptIIR` `"EndPath"` | |
| 17:41:54.5 | s2c | `GameActionListIIR` type **53231** | |
| 17:41:54.8 | s2c | `DispatchAddTag` **716** = *Old Tarantia Docks* | the achievement |

**The client names every part of it**, so none of this is inference:

- **53260 = `e_Type_GC_GameAction_StartClientSideScryScriptWithParams`** — and its params are a plain string:

  ```
  PathName=Vista_Path_6;PathDuration=36;Object=51320:55664
  ```

  The camera path, its length in seconds, and an RDB reference. That the script is **client-side** is exactly why
  the client reports its own `CinematicTransition` bracket rather than the server driving the camera.
- **53231 = `e_Type_GC_GameAction_StopScryScript`** closes it (the same body with an empty params string).
- **53228 = `e_Type_GC_GameAction_VisualLocation`** is what the OBJECT's own handle carries either side.
- and the object-action feedback spells the interaction out:

  ```
  token="startobjectaction(7)|interaction(2)|GenericAchievementVista"  category="50026"
  ```

  A **generic achievement vista** — so "interact with a viewpoint, watch the fly-through, get the achievement" is a
  content pattern retail has a name for, not a one-off script.

`CinematicTransition` is `[u32 0xC350][u32 player][u8 state]`, `01` entering and `00` leaving; the three in this
capture bracket the scene exactly.

⚠️ **Not modelled yet.** The records are all ones we already build (`GameActionListCodec` carries the action tail,
`GrantTag` awards the achievement), so the wiring is small — what is missing is the per-object DATA: which world
object is a vista, its params string and its tag. One row is proven, which is a start and not a table.
Open work tracked in Gitea #780.
