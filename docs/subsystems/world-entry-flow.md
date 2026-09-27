# World-entry flow

The end-to-end map of every path a GameServer connection can take from its `Authenticate2` hello to a rendered,
playable world, and the handshake signals that drive each one.

Code: `Faolan/GameServer/GameServerListener.Inbound.cs` (the opcode handlers) and
`Faolan/GameServer/Session/WorldEntryService.cs` (the entry builders + the post-ready flush). Session state:
`Faolan.Core.Network/NetworkClientSession.cs`. Create-flow specifics: `Faolan/GameServer/CreateFlow/CreateFlowService.cs`.

Related: [`teleport-changemap.md`](teleport-changemap.md) (the prep records the OLD connection sends before the
handoff), [`create-editor-bursts.md`](create-editor-bursts.md) (the editor burst content),
[`create-cutscene-camera-paths.md`](create-cutscene-camera-paths.md) (the create-world intro).

## The four entry modes

A connection's role is decided **once**, at `GameAgent.Authenticate2` (the Hello), and stored on the session as
`WorldEntryMode` (`Faolan.Core/Enums/WorldEntryMode.cs`). It is the single source of truth for routing every
later handshake signal, and every routing site switches on it exhaustively — all four modes plus a `default`
that logs an error. There are no derived role booleans: every call site names the mode it means.

The reason is the failure shape of the alternative: overlapping booleans in `if`/`else` chains whose final `else`
is the login path turn a role nobody considered into a login. That is how a `CreateWorld` connection gets told it
is a "changemap/teleport reconnect" mid-creation, the login ready burst fires into the intro cutscene, and the
player gets a blue screen.

| Mode | Recognised at Hello by | World entry sent | Ready signal that finishes entry |
|---|---|---|---|
| `Login` | none of the below (existing named character) | `SendWorldEntry` (burst-less, from the DB) | client `0x205F` arms, `0x2020` poll flushes |
| `CreateEditor` | `Character.Name == null` (brand-new, unnamed) | none yet — shows the character creator | `CharInPlayIIR` launches the editor burst |
| `CreateWorld` | char id in `PendingCreateWorldEntry` (2nd connection, post-naming) | `SendCreateWorldWithCutscene` (intro) | its own `0x2024` in the create burst; `0x2020` polls answered |
| `TeleportReentry` | `TeleportService.IsReentryPending` (changemap reconnect) | `SendWorldEntry` (same as `Login`) | **no client `0x205F`** — armed at Hello, flushed on `CharInPlayIIR` **or** `0x2020` |

`PendingCreateWorldEntry` is added when the name is committed (`CreateFlowService`, on the editor connection) and
consumed on the following world connection. The teleport re-entry mark is armed by `TeleportService.DoTeleport`
(every teleport in the server goes through it — GM `.teleport`, the recalls, the travel box, the door path) and
consumed in the existing-login branch of `Authenticate2` via `TeleportService.ConsumeReentry`, where it also
flips the mode to `TeleportReentry` and arms the follow-up.

## Role-at-auth vs name-now

There are two different questions the handlers ask, and confusing them breaks the reconnect flow:

- **Role at auth** — `WorldEntryMode`. Stable for the whole connection.
- **Named right now** — `IsUnnamedCreatePhase` (`Character.Name == null`). **Transient**: the create-editor
  connection names the character mid-connection (`CommitCharacterName`), so `IsUnnamedCreatePhase` flips from
  `true` to `false` while the mode stays `CreateEditor`.

So `IsUnnamedCreatePhase` implies the `CreateEditor` mode, but **not** the reverse. Handshake steps that must
behave differently before vs after the name is committed (the `0x205F` create branch, the create-vs-login
`SendReadyForPlayScreen` body) key on `IsUnnamedCreatePhase`. Steps that
care about the connection's role for its whole life (which world entry to send; how `CharInPlayIIR` is routed)
key on the mode. Both predicates live in one place — `NetworkClientSession.cs` — with the explanation.

`CharInPlayIIR` routes on the mode (the role), not on `Name == null`: a `TeleportReentry` connection is named,
and routing it on the name would send it down the editor path and put the creator UI in-world. On the mode, a
named reconnect flushes the world follow-up.

## The three handshake signals

1. **`0x205F` (`CinematicTransition`)** — despite the name the login flow uses it as its "I'm ready to load" ping,
   which is exactly why it needs per-mode routing: its trailing byte is a cinematic begin/end, and the `CreateWorld`
   connection sends a STREAM of them while the beach intro plays. `Login` / `TeleportReentry` / `CreateEditor` arm
   `HasPendingPost205FFollowUp`; `CreateWorld` must not, or the next `0x2020` poll fires the login burst into the
   cutscene. Retail sends no download in response to it in any mode.
2. **`CharInPlayIIR`** — three senders, one per role:
   - `CreateEditor` → `CreateFlowService.SendRetailCreateEditorPostReadyBurst` (launches the editor).
   - `CreateWorld` → `SendCreateWorldReadyBurst`, then `CreateFlowService.SendCreateIntroCutscene`. Retail's order
     exactly (`Blablatest` create: c2s `CharInPlayIIR` fr317 → s2c `0x2024` fr324 → cutscene fr325).
   - `Login` / `TeleportReentry` → `FlushPendingPost205FFollowUp`. A `TeleportReentry` carries no `0x205F`, so this
     is its ready signal.
3. **`0x2020` (`UpdateQuestMarkerNPC`)** — the client polling each spawned entity. Each poll is answered with a
   `0x2008` SpawnCheck (once `WorldEntered` or the mode is `CreateWorld`), and every poll calls
   `FlushPendingPost205FFollowUp` — which no-ops unless the follow-up is armed and the mode is a named one.

`FlushPendingPost205FFollowUp` is the "leave the loading screen" burst for `Login` and `TeleportReentry` only: the
deferred prompt + SpawnCheck, `0x2024` `SendReadyForPlayScreen`, the ability/combo grants, the mount re-seat, the
Shield-of-the-Risen buff, the post-ready world/quest record stream, and the wallet/guild-tag sync. It is armed on a
`Login` by the client's `0x205F` and on a `TeleportReentry` at Hello.

`CreateWorld` has its own smaller burst, `SendCreateWorldReadyBurst`, because retail's answer there is a
different set of records — `0x2024`, the `0x2052` ability grants, the `CharInPlayIIR` echo, `RegenIIR` and
`SyncCanSprint` (`Blablatest` create fr324/325). The login burst would add a combo book, a stat prompt, a quest
marker and the post-`0x2024` record stream that retail does not send there, and omit `RegenIIR`.

That `RegenIIR` carries this character's own shortfall, from `EntityVitals.ToRefillDeltas()` — which is zero in
all three pools for a freshly created one. A `RegenIIR` is a delta the client adds to its current vitals, so the
captured `0 / 0x8F2 / 0` is one character's deficit at one moment and not a constant: replayed verbatim it adds
+22.90 mana on top of a pool this same entry announces as full (`BuildVitals` sends current == max), and a level-1
Necromancer reads 269 against a 246 max until a relog recomputes the pool.

## Leaving the editor: the create→world handoff is on a timer, not on the client's bar

Once the name is committed, the editor connection must hand the client to the world server with a `0x2002`
`DispatchRedirectToServer`. The trigger for that is time measured from `CharCreationFinished`, and nothing else.

The client's first `0x206E` `AddShortcut` is not a usable trigger. That packet is the client auto-barring a grant it
was just given, which is not a statement about being ready for the world, and it makes creation depend on the
character having been granted something: with an empty starting kit the client bars nothing, sends no `0x206E`, and
sits on a black screen while the server waits for a message the client has no reason to send.

Retail's trigger is bar-independent, and the corpus is unambiguous. Across **all 13** create captures that reach a
redirect, `CharCreationFinished` → s2c `0x2002` is **8.90–9.52 s** (median 9.21; one 10.16 s outlier on a slow
session). Meanwhile the last c2s before the redirect scatters over 0.31–1.65 s and is not even the same message:
`AddShortcut` in 11 of them, a `StatIIR` `PassBlob` in one, and a bare `Ping` in another
[V2: Fwfnwfjwk_soldier-guardian_create.tshark.json]. A trigger cannot be a packet that is sometimes absent.

So `CreateFlowService.ScheduleEnterWorldHandoff` arms a single-shot 9.0 s timer at creation-finished and fires the
change-map prep + handoff whatever the client does or does not put on its bar. The `0x206E` case only persists
the starting bar. [V3: EnterWorldHandoffTriggerTests.TheHandoffFiresWithoutTheClientBarringAnything]
[V3: EnterWorldHandoffTriggerTests.TheHandoffIsSingleShot]

A fresh character also needs its single unnamed `CharacterFeatSpec` persisted, not merely seeded in memory:
`PlayerSpawnScaffoldRows.SpecsOf` throws on a character with no spec rows (it cannot tell "never trained" from
"loaded without `Include(FeatSpecs)`"), so a character that reaches a spawn without one cannot be built at all and
the client stalls on the loading screen instead. `DatabaseRepository.SaveCharacterScaffold` ensures it —
ensure-exists, never delete+reinsert, because `CharacterFeat`'s foreign key is `(CharacterId, SpecIndex)` and
replacing the specs would cascade every trained feat away.
[V3: SaveCharacterScaffoldSpecTests.AFreshCharactersSeededSpecIsPersisted]
[V3: SaveCharacterScaffoldSpecTests.SavingTheScaffoldDoesNotDropTrainedFeats]
