# World-entry flow (Gitea #339)

The end-to-end map of every path a GameServer connection can take from its `Authenticate2` hello to a rendered,
playable world, and the handshake signals that drive each one. This is the "consolidate the routing" half of
#339: the immediate reconnect bug (a teleport CharInPlayIIR launching the create-editor UI in-world) was fixed
in `cddf167b`; this page is the map that keeps the routing legible.

Code: `Faolan/GameServer/GameServerListener.Inbound.cs` (the opcode handlers) and
`Faolan/GameServer/GameServerListener.WorldEntry.cs` (the entry builders + the post-ready flush). Session state:
`Faolan.Core.Network/NetworkClientSession.cs`. Create-flow specifics: `Faolan/GameServer/CreateFlow/CreateFlowService.cs`.

Related: [`teleport-changemap.md`](teleport-changemap.md) (the prep records the OLD connection sends before the
handoff), [`create-editor-bursts.md`](create-editor-bursts.md) (the editor burst content),
[`create-cutscene-camera-paths.md`](create-cutscene-camera-paths.md) (the create-world intro).

## The four entry modes

A connection's role is decided **once**, at `GameAgent.Authenticate2` (the Hello), and stored on the session as
`WorldEntryMode` (`Faolan.Core/Enums/WorldEntryMode.cs`). It is the single source of truth for routing every
later handshake signal, and every routing site **switches on it exhaustively** — all four modes plus a `default`
that logs an error (#432). The derived `IsCreateEditorConnection` / `IsCreateWorldSession` booleans are **gone**:
two ways to ask one question is what let the routing drift, so every call site now names the mode it means.

That rule is the scar tissue from three regressions in two weeks, all identical in shape: the handlers tested
overlapping booleans in `if`/`else` chains whose final `else` was the login path, so a role nobody had considered
silently became a login. A `CreateWorld` connection was told it was a "changemap/teleport reconnect" in the middle
of character creation; the login ready burst fired into the intro cutscene; and the player got a blue screen.

| Mode | Recognised at Hello by | World entry sent | Ready signal that finishes entry |
|---|---|---|---|
| `Login` | none of the below (existing named character) | `SendWorldEntry` (burst-less, from the DB) | client `0x205F` arms, `0x2020` poll flushes |
| `CreateEditor` | `Character.Name == null` (brand-new, unnamed) | none yet — shows the character creator | `CharInPlayIIR` launches the editor burst |
| `CreateWorld` | char id in `PendingCreateWorldEntry` (2nd connection, post-naming) | `SendCreateWorldWithCutscene` (intro) | its own `0x2024` in the create burst; `0x2020` polls answered |
| `TeleportReentry` | char id in `PendingTeleportReentry` (changemap reconnect) | `SendWorldEntry` (same as `Login`) | **no client `0x205F`** — armed at Hello, flushed on `CharInPlayIIR` **or** `0x2020` |

`PendingCreateWorldEntry` is added when the name is committed (`CreateFlowService`, on the editor connection) and
consumed on the following world connection. `PendingTeleportReentry` is added by the teleport/changemap path
(`GameServerListener.GmCommands.cs` and the door path) and consumed in the existing-login branch of
`Authenticate2`, where it also flips the mode to `TeleportReentry` and arms the follow-up.

## Role-at-auth vs name-now — the subtlety that made this fragile

There are **two different questions** the handlers ask, and confusing them is what broke the reconnect flow:

- **Role at auth** — `WorldEntryMode`. Stable for the whole connection.
- **Named right now** — `IsUnnamedCreatePhase` (`Character.Name == null`). **Transient**: the create-editor
  connection names the character mid-connection (`CommitCharacterName`), so `IsUnnamedCreatePhase` flips from
  `true` to `false` while the mode stays `CreateEditor`.

So `IsUnnamedCreatePhase` implies the `CreateEditor` mode, but **not** the reverse. Handshake steps that must
behave differently before vs after the name is committed (the instance-echo route, the `0x205F` create branch,
the create-vs-login `SendReadyForPlayScreen` body) key on `IsUnnamedCreatePhase`. Steps that
care about the connection's role for its whole life (which world entry to send; how `CharInPlayIIR` is routed)
key on the mode. Both predicates live in one place — `NetworkClientSession.cs` — with the explanation.

The specific regression (#339): a `TeleportReentry` connection's `CharInPlayIIR` was routed on `Name == null`,
which is false for a named char, so it fell through — but earlier a `d43dd426` refactor had dropped the
named-char branch as "dead", so it hit the editor path and put the creator UI in-world. `CharInPlayIIR` now
routes on the mode (the role), so a named reconnect flushes the world follow-up instead.

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

`FlushPendingPost205FFollowUp` is the "leave the loading screen" burst for `Login` and `TeleportReentry` ONLY: the
deferred prompt + SpawnCheck, `0x2024` `SendReadyForPlayScreen`, the ability/combo grants, the mount re-seat, the
Shield-of-the-Risen buff, the post-ready world/quest record stream, and the wallet/guild-tag sync. It is armed on a
`Login` by the client's `0x205F` and on a `TeleportReentry` at Hello.

`CreateWorld` has its **own** smaller burst, `SendCreateWorldReadyBurst`, because retail's answer there is a
different set of records — `0x2024`, the `0x2052` ability grants, the `CharInPlayIIR` echo, `RegenIIR` and
`SyncCanSprint` (`Blablatest` create fr324/325). Sending the login burst instead added a combo book, a stat prompt,
a quest marker and the post-`0x2024` record stream that retail does not send there, and omitted `RegenIIR`.
