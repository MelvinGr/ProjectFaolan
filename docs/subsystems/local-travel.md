# Local travel — the NPCs who move you for a fee (#506)

A wagoneer, ship captain or caravan master who relocates the player. Distinct from the zone-line doors the
`world-teleports` module handles: those are one captured `UseObject` → `N3Teleport` on a fixed door, while this
is a per-NPC **list** the player chooses from, with a price and a confirmation.

## The flow

Decoded end to end from `NecroL80_mage-necromancer_login_gameplay_chat-annotated-4`, which contains two complete
trips (`took local travel to ymirs pass`, `took local travel to amphiteatrea of kartunia`):

| step | wire | evidence |
|---|---|---|
| 1. talk to the NPC | an ordinary Knubot conversation (`KnubotOpenChatWindowIIR` → `KnubotAppendTextIIR` + `KnubotAnswerListIIR`) | 20:16:37 with **Wagoner Cordelia** (handle `0x30B0B`) |
| 2. pick a destination | c2s `KnubotAnswerIIR` | 20:16:39 |
| 3. **the fee is charged here** | `DispatchUpdateCash` | 20:16:41, wallet −**50,000** |
| 4. confirmation box | s2c `DispatchScryShowMessageBox` (0x205E) | 20:17:20, `<localized id="1090" category="520" …>` |
| 5. confirm | c2s `ScryMessageBoxAnswer` (0x203B), answer 1 | 20:17:21 |
| 6. the trip | `DispatchRedirectToServer` → `Authenticate2` → `MapChange` | 20:17:23 → map **1090** |

⚠️ **The fee is charged at the PICK, not at the confirm** — ~40 s earlier in the capture. So the price is the
cost of choosing, and the box only confirms.

⚠️ **The `id` in the confirmation box is the DESTINATION MAP ID**, not a message id. All three captured boxes name
the map that the redirect then delivers — 1020 Field of the Dead, 1090 Ymir's Pass, 1092 the Amphitheatre of
Karutonia — and category 520 is the client's map-name category.
[V3: ScryMessageBoxCodecTests.YmirsPassConfirm_IsByteIdentical]

The answer is a pure echo of the box's `(threadCtxId, cookie)` plus the button index, which is what lets a
pending trip be resumed with no server-side script thread — see `ScryMessageBoxCodec`.

## A destination is not always another map

Melvin's description of the mechanic: local travel moves you **within the map/region to the entrance of whatever
you picked**, and there are separate travellers that cross maps. `TravelDestination` covers both — a same-map
destination just names the map the NPC stands on and relies on its arrival point — and `TravelService` picks the
wire path accordingly:

- **same map** → relocate in place, no loading screen (the revive-teleport path);
- **different map** → the full changemap handoff (the same path GM `.teleport` and the recalls take).

That is why a destination carries an arrival point at all: for a same-map trip the map id carries no information.
When the point is null the runtime falls back to the destination map's default `RespawnPoint`, which is the
closest thing to "the entrance" we already have per map.

## The criminal gate

The client's own Criminal spell description states the rule and covers both halves in one sentence:

> Wagoneers & ship captains will not transport you and normal vendors will not trade with you. **You can seek out
> smugglers at outlaw camps instead.**

The vendor half was already implemented (#503); `CriminalStatus.TransportsCriminals` is the transport half, using
the same name-based smuggler exception. It is checked **before** the fee, so a refused criminal is never charged.

⚠️ Not separately capture-confirmed — the corpus has no criminal attempting to travel — so it is the conservative
reading: every NPC that is not explicitly an outlaw fence refuses.

## Data

`TravelDestination`, keyed `(NpcName, DestinationMapId)` — a travel NPC is identified by NAME, never by a
session-volatile spawn handle, the same rule conversations and shops follow. Seeded from the two fully evidenced
trips:

| NPC | destination | fee | arrival |
|---|---|---|---|
| Wagoner Cordelia | 1090 Ymir's Pass | 50,000 | (366.0, 250.3, 107.0) |
| Wagoner Treva | 1092 The Amphitheatre of Karutonia | 69,488 | (112.0, 69.85, 39.0) |

Both fees and both arrival points are measured, not invented: the wallet delta two seconds after each pick, and
the player's first position sample after each `MapChange`.

## Driving it

The retail TRIGGER is not wired: the pick happens inside the NPC's captured Knubot conversation, and marking a
conversation node as "this one sells a trip to map N" is hand-authored wiring (the `quest-wiring` module is the
precedent) that has not been done. So the GM command is the bootstrap — everything downstream of the pick is the
real path:

```
.travel Wagoner Cordelia          # list what that NPC offers
.travel Wagoner Cordelia 1090     # run the real flow: criminal gate, fee, confirm box, trip
```

Open work — the conversation-node wiring, and the third captured trip whose NPC is ambiguous (the
`disconnect-after-wayfarer` capture shows the player talking to both `Osithma` and a `Tortage Smuggler` before
returning to Tortage) — is tracked in Gitea #506.
