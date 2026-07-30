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

## The "too many quests" refusal is the GROUP FINDER, not an entry gate

The annotation `treid to enter group instance, does not work because too many quests` reads like a precondition
on instance entry. The wire says otherwise — it is the **group finder refusing the signup**:

```
20:21:31  GameActionListIIR              CategoryId=4;ActivityId=38
20:21:31  c2s RequestNeededRole          → DispatchGroupFinderReceivedNeededRole
20:21:34  c2s QueueGroupFinder           00000fc4 …
20:21:34  s2c DispatchGroupFinderSignupFailed   <remoteformat id="218378963" category="10035" …>
          ── player deletes a quest (c2s QuestIIR "DeleteQuest", then a QuestLogListIIR refresh) ──
20:22:05  c2s QueueGroupFinder           00000fc4 …            (the identical activity)
20:22:08  s2c DispatchGroupFinderSignupSuccess
20:22:09  s2c DispatchYouJoinedTeam / ~Team channel subscription
20:22:12  s2c IntendedTeleport            ← now carrying the TEAM as owner
```

So `entered as private team` is not an entry option beside the difficulty mode: it is the player going through
the **group finder**, forming a team, and *then* entering the instance that team now owns. The refusal is
`DispatchGroupFinderSignupFailed` with a localized reason id, and the same `QueueGroupFinder` body succeeds once
the quest log has room.

## What is decoded vs what is built

Decoded and gated by tests: the owner pair, the difficulty word, and the refusal's channel. **Nothing here is
implemented as behaviour** — resolving an instance from the player's TEAM needs #101 grouping to be solid, and
the difficulty mode needs somewhere to live beside `(mapId, instanceId)`. Open work is tracked in Gitea #511 and
#512.
