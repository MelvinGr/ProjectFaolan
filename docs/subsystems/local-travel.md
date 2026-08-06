# Local travel — the NPCs who move you for a fee (#506, corrected in #514)

A wagoneer, ship captain or caravan master who relocates the player. Distinct from the zone-line doors the
`world-teleports` module handles: those are one captured `UseObject` → `N3Teleport` on a fixed door, while this
is a per-NPC **list** the player chooses from inside an ordinary Knubot conversation.

## The flow

Decoded end to end from `NecroL80_mage-necromancer_login_gameplay_chat-annotated-4`, which contains two complete
trips (Wagoner Cordelia at t=767…782, Wagoner Treva at t=1023…1030):

| step | wire | evidence |
|---|---|---|
| 1. talk to the NPC | `KnubotOpenChatWindowIIR` + `KnubotAppendTextIIR` + an answer list offering `(Pay to travel)` | t=767.4 with **Wagoner Cordelia** (handle `0x30B0B`) |
| 2. pick "(Pay to travel)" | c2s `KnubotAnswerIIR` index 0 | t=768.8 |
| 3. **the fare is taken here** | `DispatchSetDialogueCamera` → "I'll take you wherever you like, once you've paid." → **`KnubotStartTradeIIR`** → c2s `KnubotFinishTradeIIR` → **`DispatchUpdateCash`** | t=769.2 … t=771.1, wallet 1,893,628 → 1,843,628 = −**50,000** |
| 4. the destination list | "Your coin is good enough. Where to?" + a 10-option `KnubotAnswerListIIR` | t=771.1 |
| 5. pick a destination | c2s `KnubotAnswerIIR` index 3 | t=781.2 |
| 6. **the trip** | `StopIIR`, `KnubotCloseChatWindowIIR`, **`GameActionListIIR`** (action `0xD00D`), the *Racer* buff, `IntendedTeleportIIR` + `N3TeleportIIR` | t=781.5 |
| 7. arrival | the player is re-streamed at the action's own coordinates | t=782.1, `SimpleCharFullUpdate` at (1136.0, 132.8, 223.0) on playfield 1020 |

⚠️ **The fare is taken through a Knubot TRADE, at the "(Pay to travel)" answer — one screen before any
destination is chosen.** It is the price of being shown the list, not the price of a particular trip, which is
why every destination one wagoneer offers costs the same.

⚠️ **There is no confirmation box.** The `DispatchScryShowMessageBox` reading *"Do you want to travel to Ymir's
Pass?"* in the same capture belongs to the **zone-line door** the player walked into ~28 s after Cordelia
dropped them at the border — they select door `0x863` at t=809.8, the box follows, and answering it drives the
cross-map redirect. Reading that box as travel's last step is what made the pre-#514 model wrong, and it also
mis-stated the destinations (see below).

## The trip is one wire string

The `GameActionListIIR` at step 6 carries a single `GameActionData` element of type **`0xD00D`** whose payload
spells the whole trip out:

```
xpos=1136;ypos=133;zpos=223;PlayFieldID=1020;Local=1
```

`ypos` is the **height**, and the player's re-stream confirms all three to the centimetre.
[V3: LocalTravelTests.TheWagonerTrip_ParsesToItsDestinationAndArrival]

The action is **not travel-only**, and the trailing flag says which use it is. All four captured actions share
the `xpos/ypos/zpos/PlayFieldID` head and then differ:

| flag | meaning | example |
|---|---|---|
| `Local=1` | a same-playfield relocate, no loading screen | the three wagoner trips |
| `HeroicEnabled=0` | a cross-map entry carrying the difficulty mode (#511) | Wagoner Mycus → Poitain (2070) |

So `TravelActionCodec` carries the flag as a `(key, value)` pair rather than as a bool called `Local`:
collapsing the two would make an instance entry read as a non-local trip *and* re-emit the wrong key.
[V3: LocalTravelTests.TheHeroicFlagTrip_KeepsItsOwnKeyAndIsNotLocal]

⚠️ **We parse this action; we do not send it.** Building the trip's own `GameActionList` would need the
element's `Field` word (`0x1A18` on Cordelia's trip, `0x08FA` on Treva's — per-trip and derivable from nothing
we hold), so the server drives the relocate through the capture-verified revive teleport pair
(`IntendedTeleport` on the live playfield token + `N3Teleport` carrying the destination) instead. That, and the
*Racer* buff retail applies, are the two known fidelity gaps. Open work is tracked in Gitea #514.

## A destination is usually the same map

Local travel moves you **within the map/region to the entrance of whatever you picked** — Cordelia's nine
destinations are all on Field of the Dead, and "the border of Ymir's Pass" is the border *on her own map*.
`TravelService` tells the two shapes apart by comparing the destination map with the player's current one:

- **same map** → relocate in place, no loading screen (`WorldSimulation.RelocateSameMap`);
- **different map** → the full changemap handoff (the same path GM `.teleport` and the recalls take).

## The wiring is the row

The retail trigger is the conversation itself, so `TravelDestination` **is** a dialogue branch: it is keyed by
`(FromNodeId, OptionId)` — the screen the pick leaves and the option's own LDB string id, the same key
`ConversationEdge` uses. Taking a branch that has a row here **is** the trip; the screen that presents the list —
and therefore charges the fare — is that branch's own `From` screen, stated by the key rather than derived.
[V3: LocalTravelTests.EveryShippedTrip_HasTheListScreenThatChargesForIt]

⚠️ It was keyed by `(NpcName, AnswerPath)` until Gitea #642, with the fare gate computed as "the answer path minus
its last index". That worked, but it was string surgery over a key that could not name a screen a menu loops back
to, and a trip whose branch did not exist could be written and sit there unreachable. Now it cannot: the row
carries a foreign key to the screen.

⚠️ The old `(NpcName, DestinationMapId)` key could not express this at all: Cordelia offers nine destinations
and every one of them is on map 1020, so the key had room for exactly one.

Since Gitea #646 the branch is also how the runtime **looks a row up**, not just how it is keyed:
`GetTravelDestinationsFrom(listNodeId)` for the fare and `GetTravelDestination(fromNodeId, optionId)` for the pick.
It used to fetch by NPC display name and then filter the result down to the screen the caller already held — asking
the database a wider question with the ambiguous half of the NPC's identity. `NpcName` survives as the label the
refusal text and the log lines read, and as the `.travel` GM bootstrap's entry, because a GM types a name.

Two gates keep the wiring honest — every shipped trip must name a dialogue branch that exists, and the list
screen that charges for it must exist too.
[V3: LocalTravelTests.EveryShippedTrip_NamesADialogueBranchThatExists]

## Data

Harvested, not hand-authored: `TravelDestinationExtractor` reads the branch from the player's `KnubotAnswer`
picks, the destination and arrival from the trip's own action, and the fare from the wallet delta across the
conversation. The `travel-wiring` importer module writes them.

```zsh
dotnet run --project Faolan.Importer -- --only travel-wiring --mode overwrite --captures <data-repo>/Captures
```

| NPC | branch | destination | fare | arrival |
|---|---|---|---|---|
| Wagoner Cordelia | `0>3` | 1020 Field of the Dead (the Ymir's Pass border) | 50,000 | (1136, 133, 223) |
| Wagoner Glendon | `0>5` | 1010 Conall's Valley (the Conarch Village border) | *unmeasured* | (106, 475, 207) |
| Wagoner Mycus | `1>0` | 2070 Poitain | *unmeasured* | (1520, 172, 1476) |
| Wagoner Treva | `0>5` | 1090 Ymir's Pass (the Amphitheatre entrance) | 70,000 | (1204, 298, 778) |

⚠️ **An unmeasured fare is NULL, not 0.** Glendon's `DispatchUpdateCash` is the first wallet update in its
capture, so there is no earlier balance to subtract; a 0 would have shipped "Glendon gives free rides" as a
fact. The runtime charges nothing and logs the gap.

⚠️ Only trips the captures actually **took** are here. A wagoneer offers six to nine destinations; the others
have a name in the LDB (`[FOTD]Take me to the Haunted Forest.`) and coordinates nowhere, because the trip is
what reveals them.

## Other travellers state their trip in the dialogue TEXT

Not every traveller is a wagoneer with a list. **Three** NPCs carry the destination inline in their dialogue
text instead, as a `{teleportplayer}` directive the server acts on — no fare, no trade window, no list:

```
Tortage Smuggler   Then we go.{delay 6}{teleportplayer 210 205 y 11 4579}
Wagoner Polus      Climb aboard, then. […]{delay 3}{teleportplayer 349 564 1000}{hidegoodbye}{quit}
Sigurd             Then I wish you luck.{gameactiononnpc …}{teleportplayer 55.7 113.9 y 158.1 4020}{showgoodbye}
```

The grammar, from all three plus the capture that confirms one:

```
{teleportplayer <x> <z> [y <y>] <mapId>}
```

The **last** token is the destination playfield and the leading ones are the arrival position, **X then Z**,
with the height carried optionally behind a literal `y`. [V2: `NecroL80_…_disconnect-after-wayfarer` t=105.8 (the
Smuggler's line) → 111.7 (`IntendedTeleport` naming 4579) → 112.3, and the player arrives at (210.0, 10.5,
205.0) — matching `210 205 y 11` read as x=210 z=205 y=11; the same shape for Polus at t=516.2 → 519.4]

⚠️ **The directive never reaches the server at run time.** A conversation screen carries only an LDB reference
(category + string id + the per-string key); the text lives in the client's install and the server ships no LDB.
So the only way to act on one is to read it at IMPORT — which is why this is an RDB module and not a
dialogue-VM feature:

```zsh
dotnet run --project Faolan.Importer -- --only rdb-dialogue-teleports --RdbDir <rdb-dir> --captures <data-repo>/Captures
```

It writes ordinary `TravelDestination` rows, keyed by the dialogue **edge** whose pick lands on the directive
screen — which is exactly what that table already is ("the wiring is the row" above), so the trip runs through
the same capture-verified runtime the wagoners use rather than through a second one. Two columns separate the
two kinds:

- **`DelaySeconds`** — how long the parting line stays up before the trip runs. 0 on every captured wagoner trip
  (retail teleports on the pick) and non-zero only here, where the text states it inline.
- **`FromDialogueDirective`** — which evidence the row came from. It also changes what a NULL `Cost` MEANS: on a
  directive row it is *free* (there is no fare anywhere in the flow), where on a wagoner row it is *unmeasured*.

⚠️ **Polus states no height** (`{teleportplayer 349 564 1000}`), so the arrival is **ground-snapped** off the
baked terrain at run time. Discarding the whole arrival for a missing Y would send the player to the map's
default respawn — somewhere else entirely — when the NPC has just said exactly where they are going. With no
terrain loaded for that map there is nothing to snap to, and the default spawn is then the honest answer.

⚠️ **Sigurd was not in the original write-up** of this mechanism, which named only the first two. He is the same
thing with no `{delay}` (retail teleports as the line lands) and an extra `{gameactiononnpc
StartClientSideScryScript}` that plays a cutscene — unwired, and independent of the trip.

This is also what settles the old "the third captured trip's NPC is ambiguous" question: **Osithma's tree has no
travel option at all** (it is pure flavour), so the traveller in `disconnect-after-wayfarer` is the Tortage
Smuggler, which independently confirms the smuggler exception in the criminal gate.

## The criminal gate

The client's own Criminal spell description states the rule:

> Wagoneers & ship captains will not transport you and normal vendors will not trade with you. **You can seek
> out smugglers at outlaw camps instead.**

`CriminalStatus.TransportsCriminals` is the transport half, using the same name-based smuggler exception the
vendor half (#503) uses. It is checked at the fare gate, **before** the fee, so a refused criminal is never
charged.

⚠️ Not separately capture-confirmed — the corpus has no criminal attempting to travel — so it is the
conservative reading: every NPC that is not explicitly an outlaw fence refuses. Refusal closes the chat window,
which is likewise the conservative choice rather than a captured one.

## Driving it by hand

```
.travel Wagoner Cordelia          # list the branches, destinations and fares
.travel Wagoner Cordelia 0>3      # run that trip directly (skips the fare, which a screen earlier charges)
```
