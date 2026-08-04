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

The BCC body is the ordinary behaviour record (`BehaviorAnimations.Registration`):

```
[u32 type=Character][u32 handle][u16 form][u32 slot][u32 protobufLen][protobuf][trailer]
protobuf: f1 = behaviourId, [f2], f3 = phase, [f4], f5, f6
```

`f3` is a per-session monotonic phase counter, so it differs on every send and is never part of a selector's
identity. `form`, `slot` and `f1` are.

## The selector IS the ability's grant slot

A selector names itself. `Ability.Slot` — the leading `u32` of that ability's `0x2052` `AddSpecialAbility`
grant — **is** the word the client sends back in `PerformSpecialAction`. Every selector in the catalog below
resolves against the `Abilities` table by that column, with no exception and no near-miss:

```
0x064 = 100  Search           0x103 = 259  Switch Weapons     0x4E1 = 1249  Path of Asura
0x068 = 104  Hide             0x105 = 261  Pet Attack         0x4E2 = 1250  Toggle Combat
0x06A = 106  Rest             0x109 = 265  Pet Dismiss        0x4E4 = 1252  Crouch
0x06C = 108  Upper Left Atk   0x10C = 268  Spellweaving       0x4E6 = 1254  Dash
0x0FC = 252  Spellweaving     0x10D = 269  Mammoth Charge
0x0FF = 255  Recipes book     0x11C = 284  Quick Dismount
```

That was already half-known from the other end: `Ability.Slot` was verified against the combat capture as
`769094 → 108` and `769099 → 112`, which are `Upper Left Attack` and `Overhead Attack` — the same two numbers
the melee combo inputs send here.

**This is why an ungranted action is one the client never sends.** The client only has the number if a grant
gave it the number, so a stance button whose ability was never granted is inert *client-side* and no packet
reaches the server at all. That is the difference between "the server ignores it" and "nothing arrives", and it
is the first thing to check when an action does nothing.

Re-derive the whole table with:

```zsh
dotnet run --project Faolan.Importer -- --report player-actions --captures <data-repo>/Captures
dotnet run --project Faolan.Importer -- --report player-actions --report-arg 068 --captures <data-repo>/Captures
```

The second form narrows to one selector and prints the answering records in full — actor, form, slot, the
parsed `CommandMessage` and the tail — which is the spec a server-side implementation has to reproduce.

## Selector catalog

Measured by slicing the chat-annotated gameplay captures at each annotation and reading the client's own
`PerformSpecialAction` bodies, then cross-checking every selector against the whole capture corpus, and now
named mechanically by the slot rule above.

| selector | action | evidence |
|---|---|---|
| `0x064` | search | answered by `BehaviorId.Search` (32), `flags 8`, `activation 159`, 5-byte zero tail |
| `0x068` | **hide** (stealth) | answered by `BehaviorId.SneakHide` (30), `flags 10`, tail `00 00 00 64`; emits `SetShadowCoverageIIR` — see below |
| `0x06A` | rest | annotation `rest`; answered by `StatUpdateIIR` ×4 + `DispatchUpdateSprintBar` ×3 (regen) |
| `0x06C` `0x06D` `0x070` `0x0F0` | melee combo inputs | [docs/subsystems/combos.md](combos.md) |
| `0x0FC` | spellweave toggle — **necromancer** | see *per-class selectors* |
| `0x0FF` | open Crafting → Recipes book | `…shops_open-crrafting-receipies` |
| `0x103` | **Switch Weapons** — the weapon-set swap | answered by `BehaviorId.SwapAlternateEquipmentCommand` (46), accompanied by `DispatchSetItemStat` |
| `0x105` – `0x109` | **pet commands** — attack / defend / follow / assist / dismiss | `Abilities.Slot` 261–265; the behaviour-46 records around them are the neighbouring weapon swap, not their answer |
| `0x10C` | spellweave toggle — **demonologist** | see *per-class selectors* |
| `0x10D` | **Mount Ability: Mammoth Charge** | `Abilities.Slot` 269; sits either side of the `blue snow mamoth mount` annotation |
| `0x11C` | **Mount Ability: Quick Dismount** | `Abilities.Slot` 284; answered by `CharDCMoveIIR` + `SimpleCharFullUpdateIIR`, which is a dismount, not a stance |
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

- `GameServerListener.Inbound.cs` — the `PerformSpecialAction` handler and its routing. ⚠️ It answers only the
  spellweave toggle and Path of Asura today; every other selector is logged and dropped, so the stance never
  visibly takes even though the client sent it.
- `Ability.Slot` — the selector, from the ability's own grant.
- `--report player-actions` — the census + per-selector spec this page is measured from.
- `StartingData.SpellweaveToggleAction` — the per-class spellweave selector.
- `SpellweaveService` — the trance state machine ([docs/subsystems/combos.md](combos.md)).
- `BehaviorAnimations.Registration` — the BCC record every action is answered with.

Open work tracked in Gitea #505.
