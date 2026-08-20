# Playfield instances: who owns one, and how hard it is (#511 / #512)

#268 delivered playfield instancing keyed `(mapId, instanceId)`. This page covers the two axes on top of that
key which the annotated captures pin, and which had both been read as something else:

- **whose copy is it** — shared, one player's, or a team's (#512);
- **which difficulty** — normal or epic (#511).

Both live in the **`PlayfieldProxy`**, the `[u8 'b'][u32 ×8][u8]` reference embedded in `PlayfieldFIIR`
(MapChange), `IntendedTeleport` and `TeamMemberRecord`.

## Ownership is an ID32 in slots [2]+[3]

They were documented as *"a per-map word"* and *"a character handle, 0 except on the revive body"* — which is
what two of the three embeddings happen to look like. Across the whole corpus the pair only ever takes three
shapes, and every one of them is an owner:

| slot[2] | slot[3] | meaning |
|---|---|---|
| `0` | `0` | no owner — an ordinary **shared** playfield (the common case) |
| `2` | an owner id | a **personal** instance |
| `0xDEA9` (57001) | a team id | a **team-owned** instance |

**Type 2 appears on exactly the maps that are always private** — King Conan's castle (2040), City of Tortage
Night (4022), the Conarch Armory (5007). That is why it looked per-map: those maps are never anything else, so
storing a 2 in `Map.PlayfieldFieldA` and sending it from there stays correct for them. And the revive body's
"character handle" is this same slot — there, the owner is the player.

**The team case is decisive.** Entering the Amphitheatre of Karutonia (map 1092) as a group carries
`0xDEA9 / 0x000F08DB` here — `0xDEA9` is the team/guild class, and `0x000F08DB` is the exact team id
`DispatchYouJoinedTeam` had announced three seconds earlier and that the `~Team` chat channel subscribed to.
That answers #512's question outright: **the instance is keyed to the TEAM, not the character.**
[V3: PlayfieldInstanceOwnerTests.GroupInstanceEntry_IsOwnedByTheTeam]

## Difficulty is the class word, slot[0]

Slot[0] was a named constant, `PlayfieldClass = 0xC79C`. It is not constant: across all **183** captured
`PlayfieldFIIR`s it is `0xC79C` **182 times and `0xCABC` exactly once** — and that once is the annotated
`enered epic instance` entry, to the **same map** (1004, the Spider Caverns) that the
`energed normal instance` entry 34 seconds earlier had reached with `0xC79C`.

Same map id, same owner, same everything else. One observation, but the cleanest kind: a controlled A/B on one
map, seconds apart, with the player narrating which was which.
[V3: PlayfieldInstanceOwnerTests.NormalAndEpic_DifferOnlyInTheClassWord]

⚠️ So the mode is **not** part of `(mapId, instanceId)` — the map id is identical for both. Whatever models it
has to sit beside that key, not inside it.

### ⭐ …and the other end of the same decision: the player CHOOSES it in the travel box

The class word is what the server sends back. What the player pressed is in the **`ScryMessageBoxAnswer`** that
opened each of those two entries — same box (`<localized id="1004" category="520">`, *"Do you want to travel to
the Spider Caverns?"*), `nAnswer` **1** on the normal entry and **2** on the epic one, both followed by the
redirect that enters the zone. So the travel box's OPTIONS are the difficulty modes, and the pair reads
end to end: option index in → class word back.
See [scry-message-box.md](scry-message-box.md), which is also where the bug this uncovered is recorded — the
answer had been read as a boolean, so a `2` would have been treated as a decline.
[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json 19:56:41 / 19:57:15]

## The "too many quests" refusal is the GROUP FINDER, not an entry gate

The annotation `treid to enter group instance, does not work because too many quests` reads like a precondition
on instance entry. The wire says otherwise — it is the **group finder refusing the signup**:

```
20:21:31  GameActionListIIR              CategoryId=4;ActivityId=38
20:21:31  c2s RequestNeededRole          → DispatchGroupFinderReceivedNeededRole
20:21:34  c2s QueueGroupFinder           00000fc4 …
20:21:34  s2c DispatchGroupFinderSignupFailed   <remoteformat id="218378963" category="10035" …>
          ── player deletes a quest (c2s QuestIIR "DeleteQuest", then a MainQuestUpdateIIR refresh) ──
20:22:05  c2s QueueGroupFinder           00000fc4 …            (the identical activity)
20:22:08  s2c DispatchGroupFinderSignupSuccess
20:22:09  s2c DispatchYouJoinedTeam / ~Team channel subscription
20:22:12  s2c IntendedTeleport            ← now carrying the TEAM as owner
```

So `entered as private team` is not an entry option beside the difficulty mode: it is the player going through
the **group finder**, forming a team, and *then* entering the instance that team now owns. The refusal is
`DispatchGroupFinderSignupFailed` with a localized reason id, and the same `QueueGroupFinder` body succeeds once
the quest log has room.

## The "Select instance" window — `DispatchInstanceSelectionForCurrentPlayfield`

Clicking **Select instance** sends `GetInstanceListForCurrentPlayfield` (GameCharAgent `0x7B`) with an **empty
body**, and retail answers `DispatchInstanceSelectionForCurrentPlayfield` (GameCharInterface `0x80`):

```
[u32 count][PlayfieldProxy x count]
```

so an instance in the list carries its map, its owner and its difficulty in exactly the slots described above.
The client's own name for the parameter is `i_cInstanceList`.

⚠️ **The count is a plain u32, not the `(n+1) * 0x3F1` list frame.** That is the client's reader rather than an
inference: `FUN_00a6d1b0` reads one big-endian word through `FUN_0048f900` and loops it as a countdown, with no
multiply or divide near it. A one-element list is `00000001`, where a framed one would have been `000007E2` —
which is exactly the trap this codebase keeps re-finding, so `InstanceSelectionCodecTests` pins it.

The single captured reply is a **one-element list naming the live instance of the map the player is standing in**
(Ymir's Pass, 1090, pinned to token `0x0050B380`) — i.e. `PlayfieldProxy.ForInstance`, byte for byte.
[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json frame 10726]

⚠️ It is sent from GameCharAgent instance **`0x57`**, not the `0x58` that nearly every other GameCharAgent reply
uses. Copying the neighbours would have been the obvious guess and is wrong.

We host one copy of each playfield, so the server answers with the player's current instance, pinned to the token
the world-entry burst already gave that client; a player not yet in a zone gets the empty list rather than an
invented one.

## The dungeon flag — `DispatchInDungeonPlayfield`

`DispatchInDungeonPlayfield` (GameCharInterface `0x07`) is a one-byte s2c flag, and the same capture separates
what it means. Two **solo** "normal mode" entries (Field of the Dead 20:13:00, Ymir's Pass 20:17:35) produce **no
`0x0007` at all**; the one **group** dungeon entry produces `01` (20:22:16, five seconds before the
`entered as private team` annotation).

So it does not mark "this playfield is instanced" — a solo normal-mode instance is instanced and gets nothing. It
marks a **team-owned dungeon instance**, which is why no column in `Maps` can source it: it is not a property of
the map but of how the player entered. `Maps.PlayfieldInstanced` is 1 for 529 of 530 rows and `PlayfieldFlags` is
set on 12, with its `3` shared by four open outdoor zones and a cave — neither is the flag. It therefore falls out
of team-owned entry (#512) rather than needing data of its own, and is unsent until then. Whether a `00` is sent
on LEAVING is still open: the corpus's only `00` is in a different capture.

## ⭐ The lockout record — `DispatchSendPlayfieldLockoutData`

`DispatchSendPlayfieldLockoutData` (GameCharInterface `0xAA`) is the reset/lockout push #309 wants, and its
signature `(playfieldId, lockoutInfo)` said nothing about the second half. The dispatcher reader
(`FUN_004cddb0`, named by its own two bail-out strings — *"stream bad after `'playfieldId'`"* and *"stream bad
after `'lockoutInfo'`"*) and the composite reader it calls (`FUN_0077df00`) give it exactly:

```
[u32 playfieldId]
lockoutInfo:
  [std::string]      FUN_0048f840
  [u32]              -> +0x1c
  [u32]              -> +0x20
  [u32]              -> +0x24
  [u8]               -> +0x28, stored as a bool
  [std::string]      FUN_0048f840
```

⭐ **It is not a shape we already hold** — the question the previous pass asked. It is a small new composite:
two strings around three u32 and a flag, with no nesting and no list. That is the whole record, so the message
is byte-constructible today.

⚠️ **What the six fields MEAN is not recoverable.** The reader names only `lockoutInfo` as a whole — there are
no per-field bail-out strings the way `DispatchGuildBadName`'s reader has — and **no capture carries this
message**, so there is nothing to correlate against. A reset timer and an instance name are the obvious readings
and are exactly the kind of guess this project does not ship.
`[V0: DispatchSendPlayfieldLockoutData's six lockoutInfo fields]`

## What is decoded vs what is built

Decoded and gated by tests: the owner pair, the difficulty word, the refusal's channel, and the instance-list
reply.

⭐ **Team routing is now BUILT (#512).** A `MapInstanceMode.Group` map resolves its instance from the player's
TEAM, not their character: `WorldState.ResolveEntryInstance` takes the team id and `TeamInstance` keys one copy
per `(teamId, mapId)`, so the whole party lands in one zone and a member arriving later JOINS it rather than
minting a second. A player entering a group map with no team gets a private copy — there is no shared dungeon, a
group map is always somebody's. The team id crosses the tier boundary through `ICsPlayerAgentProvider.TeamIdOf`,
the same seam `LeaveTeam`/`KickFromTeam` already use, because the roster lives on the CS tier.

The wire follows: `BuildMapChange` switches the proxy's owner pair **as a unit** to `(0xDEA9, teamId)` when the
zone is a team's, where a personal copy keeps `(PlayfieldFieldA, characterId)`. Switched whole rather than having
the id swapped under the map's own type word — a team id under type `2` is a shape retail never sends.

⚠️ **Lifetime is "empty = gone", which is weaker than retail's rule.** The binding is validated against the live
zone rather than cleaned up on leave, so a copy nobody is in is not the team's copy any more and a re-entry mints
a fresh one. Retail is stricter: its leave prompt is *"Do you wish to leave? You will be unable to return to this
instance."*, so a single member who steps out cannot rejoin **while the others are still inside**. Ours lets them.
Closing that gap needs the leave-side message box, which is not sent yet.

⭐ **The difficulty MODE is now BUILT (#309), with epic serving the normal map's content as a deliberate
interim.** The client renders the normal/epic buttons itself (the two captured Spider Caverns shows are
byte-identical in shape to every plain travel box), so the server's whole job is honouring the answer INDEX:
option `2` arms an EPIC teleport (`ScryMessageBoxAnswerCodec.ConfirmSecondOption`), the choice rides
`PendingEpicEntry` across the handoff (the reconnect's world entry is a new connection), and it lands in three
places — the prep packets' class word (`TeleportCodec.Build(..., epic: true)` reproduces the captured epic begin
byte-for-byte), the entry `MapChange`'s class word, and the instance selection. An epic copy never shares a zone
with a normal one: `Zone.IsEpic` is stamped at entry, the stay-in rule refuses a difficulty mismatch (retail's
own captured epic re-entry minted a FRESH instance — consecutive playfield tokens), a Shared-mode map's epic
entry gets a private/team copy, and `TeamInstance` binds per `(teamId, mapId, epic)`.

⚠️ **What epic actually changes on retail is measured and deliberately NOT modelled yet.** The back-to-back
Spider Caverns entries carry the SAME creatures at the same names, templates and levels — re-tuned in exactly
TWO protobuf fields (a full recursive field diff finds nothing else):

* **health** (`stats.life/health` and the pool fields that mirror it): Cavecrawler/Cavehunter (npc_type 4)
  147,600 → 1,186,900 at L27 and 154,500 → 1,292,300 at L28 (~8×), minion-type Spiderlings 73,800 → 78,700
  (~+6%). Not a flat multiplier (the ratio differs per level), so the rule needs more than one dungeon's
  evidence.
* **`npc_stats.normalset`** — the nameplate DECORATION set (#508 pinned the skull to it; #758's "Prey" label is
  its value 4): regulars go absent → **13** and minions 1 → **9**, with zero exceptions over all 171 spawns.
  13 is the ELITE decoration normal content already uses on the Ymirish (Destroyer/Sorcerer/Frostmaster in
  Ymir's Pass), so epic re-tags the same mobs with the elite set rather than inventing new creatures. The
  capture's own normalset census: 1 minion · 2/10 boss (npc_type 3) · 3 mini-boss (npc_type 2) · 4 prey ·
  9 epic minion · 13 elite · 16 player pets · 32 guards.

Until the tuning rule is modelled an epic zone loads the normal population (normal decorations included), and
`NpcSpawnExtractor` skips epic windows so the tuning can never poison the normal stat columns. Remaining
#309/#511 work: the epic stat + normalset tuning, encounter content (bosses/raids), and the retail leave-side
lockout above.
