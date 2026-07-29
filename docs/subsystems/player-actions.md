# Player actions & stances (`PerformSpecialAction` 0x205A)

`GameCharAgent.PerformSpecialAction` (`0x205A`) is the client's **generic player-action channel**. The body is a
single `u32` selector and nothing else — every stance, toggle, combo key, weapon swap and recall the player
triggers from the action bar arrives on this one opcode, distinguished only by that word. It is therefore *not*
a spellweave opcode, which is how routing every `0x205A` to the trance once made opening the crafting recipe
book start a weave on a necromancer.

## The server must answer

**Every** selector observed in the corpus is answered by a player-addressed `BehaviorControlCenterIIR`
(`0x642CD3D6`). None of these actions is client-side animation only — the client sends the selector and waits
for the server to assert the behaviour before the stance visibly takes. That is the single most useful fact on
this page: implementing a stance means emitting the player's BCC, not just recording state.

The BCC body is the ordinary behaviour record (`CreateEditorRecordBuilder.BuildBehaviorControlCenterIir`):

```
[u32 type=Character][u32 handle][u16 form][u32 slot][u32 protobufLen][protobuf][trailer]
protobuf: f1 = behaviourId, [f2], f3 = phase, [f4], f5, f6
```

`f3` is a per-session monotonic phase counter, so it differs on every send and is never part of a selector's
identity. `form`, `slot` and `f1` are.

## Selector catalog

Measured by slicing the chat-annotated gameplay captures at each annotation and reading the client's own
`PerformSpecialAction` bodies, then cross-checking every selector against the whole capture corpus.

| selector | action | evidence |
|---|---|---|
| `0x064` | search | annotation `seach`; appears with the hide/rest triple |
| `0x068` | **hide** (stealth) | emits `SetShadowCoverageIIR` — see below |
| `0x06A` | rest | annotation `rest`; answered by `StatUpdateIIR` ×4 + `DispatchUpdateSprintBar` ×3 (regen) |
| `0x06C` `0x06D` `0x070` `0x0F0` | melee combo inputs | [docs/subsystems/combos.md](combos.md) |
| `0x0FC` | spellweave toggle — **necromancer** | see *per-class selectors* |
| `0x0FF` | open Crafting → Recipes book | `…shops_open-crrafting-receipies` |
| `0x103` – `0x109` | weapon-set / equipment selection | all answered by behaviour **75123**, slot 46, and accompanied by `DispatchSetItemStat` |
| `0x10C` | spellweave toggle — **demonologist** | see *per-class selectors* |
| `0x10D` `0x11C` | unidentified | [V0: 0x205A selectors 0x10D and 0x11C are unidentified] |
| `0x4E1` | Path of Asura (recall to the **bound** location) | behaviour 177216, slot 4 |
| `0x4E2` | combat toggle (sheathe / draw) | behaviour id varies per class & weapon |
| `0x4E4` | crouch — a **toggle**, same selector both ways | pressed to crouch and again to stand |
| `0x4E6` | dash | answered by `DispatchUpdateSprintBar` |

⚠️ `0x4E1` recalls to the **bound** location. The Totem of Origins is a different destination — the racial home
city — and is an item use, not a `0x205A` selector. See `StartingData.RacialHomeMap`.

### Calibrating the annotations

The chat annotations are retrospective — typed *after* the action — so a selector always precedes its label.
`0x4E1` calibrates the lag independently of any guess: it was already known to be Path of Asura before this
pass, it fires at 20:28:42, and the annotation `casting path of asura` lands at 20:28:47. Five seconds. Every
other pairing on this page was read with that offset and then confirmed against a second capture.

The second capture is the load-bearing part. `NecroL80_mage-necromancer_login_cast-general-abilities` carries no
chat annotations at all, yet walks the same selectors in the same shape — including `0x068` twice, each time
with a `SetShadowCoverageIIR`. Nothing there depends on reading Melvin's typing.
[V2: NecroL80_mage-necromancer_login_cast-general-abilities.tshark.json]
[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json]

### Per-class selectors — the spellweave toggle

The spellweave activate/deactivate selector is **per class**, not one shared id:

| class | selector |
|---|---|
| Necromancer | `0xFC` |
| Demonologist | `0x10C` |

Each class sends only its own. Across the corpus the three necromancer spellweave captures carry `0xFC` and
never `0x10C`; both demonologist captures carry `0x10C` and never `0xFC`. What proves they are the same
mechanic rather than two unrelated actions is the server's answer — both produce the identical player BCC pair,
behaviour **42041** to open the trance then **41302** to close it, at the same slot 1 and form 4.

Priest of Mitra and Tempest of Set also weave, but no capture shows either doing it, so
`StartingData.SpellweaveToggleAction` deliberately leaves them unmapped rather than guessing: an unmapped class
does nothing and says so, where a wrong id would silently open a trance on the wrong keypress.
[V2: DemoL80_mage-demonologist_login_randomActions.tshark.json]
[V2: NecroL80_mage-necromancer_login_spellweaving.tshark.json]

## `SetShadowCoverageIIR` (0xD6C1F0CC)

The stealth channel, and the reason `0x068` = hide is the most certain row in the table. Entering hide, the
**client** reports its shadow coverage and the server echoes it straight back:

```
[u32 len=17][u32 opcode][u32 class=50000][u32 handle][u8 dir][u32 coverage]
                                                       ^ 1 = c2s, 0 = the s2c echo
```

Uniform across three captures and two characters. ⚠️ `coverage` is **100 in every observation** — that is what
was measured, not a scale that has been established. Do not read it as a percent, and do not treat 100 as a
constant either: the client computes it from local lighting, which is the whole point of the client being the
one to send it, so a value other than 100 is expected somewhere the corpus simply has not walked.
[V2: Hakotep_hox_login_gameplay_untrain-feats-(causes_loading_screen)_train-feats_delete-quests.tshark.json]

## Code

- `GameServerListener.Inbound.cs` — the `PerformSpecialAction` handler and its routing.
- `StartingData.SpellweaveToggleAction` — the per-class spellweave selector.
- `SpellweaveService` — the trance state machine ([docs/subsystems/combos.md](combos.md)).
- `CreateEditorRecordBuilder.BuildBehaviorControlCenterIir` — the BCC record every action is answered with.

Open work tracked in Gitea #505.
