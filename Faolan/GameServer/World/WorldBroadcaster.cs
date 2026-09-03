using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// WHO CAN SEE WHAT, and the wire that follows from it (#826). The interest pass decides which entities and world
	// objects each player currently holds; this class is what turns that decision into packets - the spawn, the
	// despawn, the move relay, the entry bursts - plus the "relay this record to everyone watching X" family that
	// every other broadcaster is built on.
	//
	// It reads the world model through `WorldSimulation`, loads templates from the DB, and frames every packet on the
	// world conversation (`GameEndpoints.WorldSenderEndpoint`: sender = world-system 0x58, receiver = the
	// recipient's GameInterfaceType endpoint). The FA015807 body carries the SUBJECT's handle unchanged, so the
	// recipient renders the subject as a separate entity.
	//
	// ⚠️ It used to be all of the world's wire at once - 2523 lines, 81 members. What it is NOT any more, and must not
	// grow back into: what an NPC does (`NpcBroadcaster`), the loot lifecycle (`LootBroadcaster`), interacting with a
	// world object (`WorldObjectService`), the quest journal and its markers (`QuestBroadcaster`), and the knockback
	// (`Combat/KnockbackService`). Each was cut on the same rule - a different QUESTION, and a ONE-WAY dependency:
	// they call in here for a spawn or a despawn, nothing here calls out to them. The one exception is deliberate and
	// is `QuestBroadcaster.SendQuestMarkerIfGiver`, which a streamed-in NPC needs.
	public sealed class WorldBroadcaster
	{

		// (#266's PatrolWalkSpeed estimate lived here. It is now WorldState.RouteWalkSpeed, because the same number has
		// to time the re-issue cadence AND advance the model, and having two copies is how they drifted - the comment
		// here still claimed "NpcMoveSpeed (12) is the RUN speed" long after NpcMoveSpeed became 7. It is also no longer
		// an estimate: it is measured off retail's own patrollers, see #714.)

		// Dev visualization (#363): when true, an NPC that resolves to an RDB spawn AREA has that area's label pushed as
		// its overhead nameplate (via SetNameIIR after spawn) so a manual creature-labeling pass sees which area it's in.
		// Off by default; toggled live by the `.togglenpcgroup` GM command (ToggleAreaLabels) - no config.
		//
		// INSTANCE state, not static. It is a per-world-host toggle and this class is per-host, so a static made it
		// process-global - which under `--role login --role realm=<id>` (or the all-in-one dev run) would mean one
		// GM's debug nameplates following the other realm's players around. Nothing outside this class touches it.
		private bool _debugAreaLabels;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// _tortageIntro is constructed before the broadcaster (it needs only the sim + DB), so the interest stream can
		// call into it (Casilda's captive pose on stream-in) without a construction cycle.
		private readonly ComboService _combos;
		private readonly TortageIntroService _tortageIntro;

		private readonly WorldSimulation _sim;

		// Handles for every runtime-spawned entity (loot bags here, NPCs / pets / ambient members on NpcBroadcaster).
		// The range and why it matters are documented on the allocator itself. ⚠️ SHARED, and injected rather than
		// built here: a SECOND counter would hand out colliding handles.
		private readonly RuntimeHandleAllocator _handles;

		// #363 ambient spawn-group pools. Owns the pool state and the spawning; the STREAMING of what it stands up is
		// NpcBroadcaster.MaintainAmbientPools. Shared with it, so injected - we only need it to seed a zone.
		private readonly AmbientPoolService _ambient;

		// Decides the quest-marker byte, the forced quest-item drop and a player's held-quest sets. Shared with
		// QuestBroadcaster (it caches the quest-giver and goal-target tables, so a second instance would duplicate
		// them), which is why it is injected rather than built here.
		private readonly QuestMarkerResolver _questMarkers;

		// The quest WIRE. One-way: we call in (a streamed-in NPC needs its '!' / '?'), it holds nothing of ours.
		private readonly QuestBroadcaster _quests;

		// Fills a zone with its NPCs and world objects on first entry. Loading is state; the streaming that follows is
		// ours. Forwarded to below so the listener's call sites are unchanged.
		private readonly ZonePopulationLoader _zones;

		// Encounter lure/boss swaps and quest ambushes. Forwarded to below: all three are PORT methods
		// (IWorldOutbound / IQuestOutbound), so the signature has to stay on us even though the work does not.
		private readonly EncounterService _encounters;

		public WorldBroadcaster(WorldSimulation sim, IDatabaseRepository database, ILogger logger, TortageIntroService tortageIntro, ComboService combos,
			QuestMarkerResolver questMarkers, QuestBroadcaster quests, RuntimeHandleAllocator handles, AmbientPoolService ambient)
		{
			_handles = handles;
			_ambient = ambient;
			_sim = sim;
			_database = database;
			_logger = logger;
			_tortageIntro = tortageIntro;
			_combos = combos;
			_questMarkers = questMarkers;
			_quests = quests;
			_zones = new ZonePopulationLoader(sim, database, database, combos, _ambient, logger);
			_encounters = new EncounterService(sim, database, _handles, combos, BroadcastEntityDespawn, logger);
		}

		// #261: re-evaluate quest-gated NPC visibility for one player after their quest state changed (accept / advance /
		// complete). Reloads their active-quest set, then runs an interest pass so newly-qualified gated NPCs spawn and
		// no-longer-qualified ones despawn in-place - no player/NPC movement required.
		public async Task RefreshQuestVisibility(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			await _questMarkers.LoadActiveQuestIds(entity);
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null, client, entity);
		}

		// #778: re-run one player's interest pass with NOTHING having changed on their side - the world tick calls it on
		// a slow cadence so that entities which moved THEMSELVES are streamed in and out.
		//
		// Every other interest pass is driven by the player: BroadcastPlayerMove (they moved), BroadcastPlayerEntry
		// (they arrived), RefreshQuestVisibility (their quests changed). Nothing was driven by the SUBJECT moving, and
		// `BroadcastNpcMove` only sends to viewers who already hold the handle - so for a player standing still, an NPC
		// that walked out of range froze at its last rendered position for ever, and one that walked in was invisible
		// until the player next moved. #703 made that routine rather than rare: a route walker travels its whole beat,
		// and an Old Tarantia beat runs up to 98 waypoints.
		//
		// ⚠️ It cannot hang off the NPC's own move effect, which is what the obvious fix would be. A ROUTE walker emits
		// no per-step effect at all - the beat is issued to the client once and walked THERE (#716), and the server only
		// advances its model - so the entities most in need of this are exactly the ones with no move to hook.
		//
		// `moveBody: null` makes this Spawn/Despawn only; the movement itself already rides BroadcastNpcMove / the
		// issued patrol. The mover arguments are left null on purpose: they only drive world-OBJECT streaming, and a
		// static object cannot walk into range.
		//
		// ⚠️ It is the ONE sender that reaches a client on nobody's schedule but the tick's, so it is also the one that
		// can reach a client that is still ENTERING. World entry places the player in the zone (which is what makes
		// them visible to this sweep) seconds before it flushes its own packets, and in that window the sweep would
		// stream every in-range NPC to a client that has not been told which playfield it is in - handing it spawns
		// stamped with the zone's minted fallback token instead of the map's. The client drops every one of them, and
		// because the sweep SEEDS them into the player's Known set, the entry burst then finds nothing in range and
		// never corrects it: every NPC in the entry radius stays invisible for the whole session while remaining
		// fully alive server-side. Hence the gate on the entry actually having been DELIVERED, not merely begun
		// (Session.WorldEntered is set well before the packets are built - see the two flags' own comments).
		public async Task ResolveInterest(INetworkClient client)
		{
			if (!client.Session.WorldEntryDelivered)
				return;

			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null);
		}

		// The world objects within interest range at ENTRY, as ready-to-send records in the position retail puts them -
		// early in the entry stream, before the snapshot. Mirrors BuildEntryNpcBurst: it seeds the known set so the
		// per-move pass afterwards streams only the delta. No despawns are possible on a fresh entry (the known set
		// starts empty), so any that appear would mean stale per-zone state and are returned for the caller to send.
		public (List<Rpc> Records, int Total) BuildEntryWorldObjects(INetworkClient client, WorldEntity player, IReadOnlySet<uint> quests)
		{
			var zone = _sim.World.ZoneForEntity(player);
			var (spawn, _) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, quests);

			return ([.. spawn.Select(o => WorldObjectRpc(client, o, zone.PlayfieldToken))], zone.WorldObjects.Count);
		}

		// Brings the player's rendered world objects in line with interest after a move: spawns what came into range,
		// despawns what left. See ResolveWorldObjectInterest for why re-entry re-sends the whole record.
		public async Task StreamWorldObjects(INetworkClient client, WorldEntity player)
		{
			var zone = _sim.World.ZoneForEntity(player);
			if (zone.WorldObjects.Count == 0)
				return;

			// On a move the entity's own set is current - BroadcastPlayerMove refreshes it immediately before this.
			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, player.ActiveQuestIds);

			foreach (var obj in spawn)
				await client.SendAsync(WorldObjectRpc(client, obj, zone.PlayfieldToken));

			foreach (var handle in despawn)
			{
				// Despawn with the object's OWN class - a Character-classed quit silently misses a world object (#366).
				var classWord = zone.TryGetWorldObject(handle, out var gone)
					? WorldObjectColumns.TypeTagForClass(gone.SubOpcode)
					: WorldObjectColumns.TypeTagForClass(0);
				await SendWorldDespawn(client, zone.PlayfieldToken, handle, classWord);
			}
		}

		// One static world object's FullUpdate, built from its columns and stamped with the live playfield token. The
		// carrier's [u32 len] is the SENDER's (#530 rule 2), so it is written here rather than by the codec.
		private static Rpc WorldObjectRpc(INetworkClient client, LiveWorldObject obj, uint playfieldToken)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				// The door state comes from the LIVE object, not the row (#598) - a door this zone has open streams open.
				// So does a destructible's health (#498): 142 of the 193 shipped harvest nodes were captured MID-HARVEST,
				// so replaying the captured word rendered most of a zone's nodes empty while the server held them full.
				s => s.WriteArrayUInt32Length(WorldObjectCodec.Build(obj.Row, obj.Handle, playfieldToken, obj.IsClosed, obj.Health)));
		}

		// Relays a player's BEHAVIOR (the BehaviorControlCenterIIR swing/cast animation) to every other player who can
		// already see them, so the attack/cast renders on the other clients - the combat analogue of BroadcastPlayerMove
		// (#48). `body` is the same BCC echo the actor's own client receives (it already carries the actor's handle as
		// instance); retail relays that exact body to nearby players, only the frame receiver differs (the
		// other-players-visible capture shows s2c BCC for other-player handles 0x1316xxx). Gated on the viewer's Known
		// set so a still-loading client isn't sent a behavior for a handle it hasn't built. No-op solo / when unseen.
		public async Task BroadcastBehaviorToViewers(INetworkClient actor, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the actor (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		// Despawn a world-owned entity (a killed NPC, an expired loot bag) for every nearby player THAT ACTUALLY HAS IT
		// in their Known set. The Known guard is the #245 invariant: an n3ToClientQuit for a handle the recipient was
		// never sent crashes the client (`pcDynel == null`) - a still-loading player in range of an entity it never
		// received must not get a despawn for it. Interest is the sole NPC spawn path now, so Known is the authoritative
		// "was this entity sent to this client" record; the interest path (ResolveMove) already only despawns Known
		// handles, but this range-based world-tick path did not. Removes the handle as it despawns. Uses the same
		// n3ToClientQuit packet as player despawns.
		public async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Known.Remove(entity.Handle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entity.Handle);
			}
		}

		// Fade an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot bags
		// (consumed or expired). Loot bags carry the 0xC87A typetag.
		//
		// ⚠️ The fade is HALF of retail's sequence, and sending it alone is what leaves a bag on screen. The captured
		// completion is `… -> n3ToClientFade -> UpdateSimpleDynel`, and the second message is the CONSUMED visual: a
		// fade makes the object go away gracefully, the update states that it is spent. The expiry path used to send
		// only the fade, so an expired bag differed from a looted one for no reason - and a client that keeps the
		// object keeps everything hanging off it, including whatever it draws on a lootable.
		public async Task BroadcastEntityFade(WorldEntity entity)
		{
			var fade = N3ToClientFadeCodec.Build(EntityClass.LootBag, entity.Handle);
			var consumed = UpdateSimpleDynelCodec.Build(entity.Handle);
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null)
					continue;

				await viewer.Client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(fade)));
				await viewer.Client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, viewer.Client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(consumed)));
			}
		}

		// The defender's shield rose changed (an NPC engaged or its shift timer moved a guard; a PLAYER moved one of
		// theirs, #620): send the CombatShieldIIR to every viewer that has it CONFIRMED, the same gating as the
		// heartbeat - the rose is drawn ON the dynel, so a still-loading client has nothing to draw it over.
		//
		// The OWNER is skipped when the defender is a player: they set stat 0xA5 locally before sending it up, so
		// echoing it back would only re-apply the value they already drew.
		public async Task BroadcastShieldRose(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			var record = CombatShieldCodec.Build(entity.Handle, entity.ShieldDistribution);

			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Handle == entity.Handle)
					continue;

				if (viewer.Client != null && viewer.Confirmed.Contains(entity.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// Send records that describe what an ENTITY is doing to every viewer that can see it - the general form of the
		// Broadcast* methods below, for a sequence that has no single name.
		//
		// ⭐ The rule it exists to serve (#272): a record addressed to a SUBJECT, describing world-visible state, goes
		// to everyone who can see that subject. Measured, not assumed - in the two-client capture
		// (`login_2-clients_group-trade-friend`, the only one with two game connections) every subject of
		// `MoveTaskIIR` (12/12), `BehaviorControlCenterIIR` (11/11), `LookAtIIR` (8/8), `StatUpdateIIR` (9/9) and
		// `StopIIR` (4/4) appears on BOTH clients' streams. What is genuinely per-player is the player's OWN state -
		// `FullPlayerCharacterIIR` 0/2, `FeedbackIIR` 0/2, `UpdateInventoryIIR` 0/2 - and a despawn, which is per
		// viewer by interest (`N3ToClientQuitIIR` 2/44). `--report broadcast-check` re-measures it.
		//
		// ⚠️ Not every record of a broadcast TYPE is a broadcast: a `BehaviorControlCenterIIR` carrying an ACK is a
		// reply to the actor's own request, and the two-client capture contains no combat, so it cannot speak for
		// those. Reach for this when the record says what an entity DID, not when it answers what a client ASKED.
		public async Task BroadcastEntityRecords(WorldEntity subject, params byte[][] records)
		{
			ArgumentNullException.ThrowIfNull(subject);
			ArgumentNullException.ThrowIfNull(records);

			foreach (var viewer in _sim.World.ZoneForEntity(subject).PlayersInRange(subject.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Known.Contains(subject.Handle))
					continue;

				foreach (var record in records)
					await WorldIir.Send(viewer.Client, record);
			}
		}

		// On disconnect/logout: despawn the leaving player for everyone who could see it (n3ToClientQuit), before it is
		// removed from the zone. Returns the dispatch task so the caller can fire-and-forget (the connection is going
		// away). Single-player is a no-op (no viewers).
		public Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_sim.World.ZoneForEntity(leaver), leaver);
			return DispatchInterest(events, null);
		}

		// After world entry: spawn the entering player to nearby OTHER players (its NPC set now rides the entry burst,
		// #398 BuildEntryNpcBurst, and is already in Known - so the interest pass streams no NPCs, only section 2's
		// spawn-me-to-viewers fires). Interest remains the sole path for the STREAMED delta as the player then walks.
		public async Task BroadcastPlayerEntry(INetworkClient client)
		{
			// No SpawnBody check any more: a player's record is BUILT per send (#429), so having one was never the real
			// precondition - having a live entity is.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);

			// The burst NPCs are already in Known (BuildEntryNpcBurst), so includeNonPlayers here re-confirms them in
			// range without re-spawning; its real job at entry is section 2 - spawning this player to nearby viewers.
			var events = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, true, true);
			await DispatchInterest(events, null);
		}

		// After a player's Move: update interest and broadcast the resulting spawns/moves to nearby players.
		//
		// `moveBody` is the mover's own CharDCMove carrier VERBATIM (only the env byte cleared - the caller's echo body),
		// not a reconstruction. Retail relays a move byte-for-byte: across the corpus 9,438 of 9,932 c2s moves reappear
		// s2c identical bar that byte, and in xxx_2clients_jumping all 35 do, to BOTH clients. That also spares this end
		// the flag-gated layout - how many rotation floats flag1 bit4 implies, where a jump's acceleration vector sits
		// (docs/re/chardcmove-grammar.md) - which the old rebuild-from-parsed-fields had to know and got wrong.
		// `position` is the ACCEPTED position (the same one the body carries) and drives interest only.
		public async Task BroadcastPlayerMove(INetworkClient client, byte[] moveBody, Vector3 position)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			// Advance the player's model position to where they actually are BEFORE computing interest - otherwise it
			// stays frozen at the entry point and only entity ever within range of the wash-ashore spot streams (the
			// "most NPCs missing" bug). This is also what makes other players see this one at its live position.
			entity.Position = position;

			// includeNonPlayers: stream the zone's NPCs to this player as they move (the burst only rendered the NPCs
			// near the entry point; this fills in the rest of the map - e.g. Casilda, who is outside the captured
			// burst). Burst NPCs are already in the player's Known set (seeded at entry) so they are not re-spawned.
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			var spawns = 0;
			var despawns = 0;
			foreach (var ev in events)
			{
				if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Spawn)
					spawns++;
				else if (ev.Recipient.Handle == entity.Handle && ev.Type == InterestEventType.Despawn)
					despawns++;
			}

			if (spawns > 0 || despawns > 0)
			{
				_logger.LogInformation("Interest: {Player} at {Pos} -> streamed +{Spawns} / -{Despawns} entities (now knows {Known}).", entity.Name, entity.Position, spawns, despawns,
					entity.Known.Count);
			}

			await DispatchInterest(events, moveBody, client, entity);
		}

		public async Task BroadcastSpellToViewers(INetworkClient caster, byte[] body)
		{
			var entity = _sim.PlayerWorldEntity(caster);
			if (entity == null || body == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue; // only players who already see the caster (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		// A player's STAT change that other players must see - the mount being the case that exposed the gap (#428): the
		// ride buff was already relayed by SendMagicBuffIir, but the StatUpd carrying Mount/MountSpeed was not, and that
		// is the half the client renders the horse from. So the actor sat on a mount only on their own screen.
		//
		// The stat body names the ENTITY, and the client argument only frames the receiver, so the same block is rebuilt
		// per viewer - exactly what BroadcastSpellToViewers does with the ApplySpell body. Same #245 gate: never relay
		// for a handle the viewer's client has not built.
		public async Task BroadcastStatsToViewers(INetworkClient actor, CharacterStatBlock stats)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(entity).Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Known.Contains(entity.Handle))
					continue;

				await viewer.Client.SendAsync(GameWire.BuildStatUpdate(viewer.Client, stats, entity.Handle));
			}
		}

		// #398: builds the in-range NPC spawns for the world-ENTRY BURST - the close NPCs retail ships in the burst
		// BEFORE the client's first move (fr64-75 in the Hoxxling beach login capture), instead of dribbling them out
		// as a post-load interest volley. Each is SEEDED into the entering player's Known set so the interest layer
		// (BroadcastPlayerMove) streams only the DELTA as the player walks and never re-spawns or culls the initial
		// set - which subsumes the #245 despawn-during-load root cause. Registers each handle -> name/template for
		// click routing exactly as DispatchInterest does for a streamed spawn. Returns the framed FA015807 records to
		// append to the entry burst (`toSend`) plus the NPCs themselves, whose quest markers / captive pose ride
		// SendEntryNpcFollowups after the burst flush (those are separate IIRs, not spawn records). The entering
		// player is spawned to OTHER viewers separately (BroadcastPlayerEntry, section 2).
		public async Task<(List<Rpc> Records, List<WorldEntity> Npcs)> BuildEntryNpcBurst(INetworkClient client)
		{
			var records = new List<Rpc>();
			var npcs = new List<WorldEntity>();

			// ⚠️ Gated on the player having a live ENTITY, which is what this check always MEANT. It used to read
			// `entity?.SpawnBody == null`, and #429 then stopped a player from ever having a SpawnBody at all (a
			// player's record is rebuilt from live state per send, because the cached one described them as they were
			// at LOGIN). That commit converted the two other places SpawnBody had "quietly doubled as the is-this-
			// spawnable-yet flag" and missed this one - so from that day every login took the early return and the
			// entry burst shipped ZERO NPCs, reporting `0 in-range NPCs` on a map holding 168 of them. Nothing here
			// needs the player's own body: the records this builds are the NPCS'.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return (records, npcs);

			// Seed the player's active/completed quest set first so quest-gated NPCs (#261 Scavenger Tuut, #262 hide-
			// once-done) are correctly included/excluded in the burst from the very start.
			await _questMarkers.LoadActiveQuestIds(entity);

			var zone = _sim.World.ZoneForEntity(entity);
			var token = zone.PlayfieldToken;
			foreach (var npc in zone.EntitiesInRange(entity.Position, WorldSimulation.PlayerInterestRange, entity.Handle))
			{
				if (npc.IsPlayer || npc.IsLoot || npc.IsDead || npc.SpawnBody == null)
					continue;
				if (!npc.IsVisibleTo(entity))
					continue; // #261: a quest-gated NPC the player doesn't qualify for yet - treat as not in range
				if (!entity.Known.Add(npc.Handle))
					continue; // already seeded (idempotent guard)

				if (npc.Name != null)
				{
					client.Session.SpawnedNpcNamesByHandle[npc.Handle] = npc.Name;
					client.Session.SpawnedNpcTemplatesByHandle[npc.Handle] = npc.NpcTemplateId;
				}

				records.Add(BuildWorldSpawnRecord(client, npc, token));
				npcs.Add(npc);
			}

			return (records, npcs);
		}

		// Resolves the playfield token a map's world-entry will stamp for a cross-map teleport's destination announce.
		// The map's real playfield-instance token (#305), threaded through map-change/spawn/world-objects/snapshot so
		// they agree by construction. Sourced per-map from Map.PlayfieldToken (the value the client scopes the
		// character to when it loads that playfield; the client keys per-area behaviour on it - e.g. mount permission,
		// so a wrong token makes Old Tarantia read as the no-mount Tortage beach). A missing MAP is a hard error and
		// throws; a found map with an un-captured token (0) falls back to the standard playfield token - that 0 is a
		// documented "no per-map override" state (most maps), not a lookup failure, so it is a real default rather than
		// a fallback masking a missing thing.
		public async Task<uint> ResolveMapPlayfieldToken(uint mapId)
		{
			var map = await _database.GetMap(mapId) ?? throw new InvalidOperationException($"ResolveMapPlayfieldToken: map {mapId} not found");
			return map.PlayfieldToken != 0
				? map.PlayfieldToken
				: WorldState.DefaultPlayfieldToken;
		}

		// #398: the per-NPC follow-ups for the entry-burst NPCs (BuildEntryNpcBurst) - the quest markers and the
		// Casilda captive pose that DispatchInterest sends right after a streamed spawn. Sent AFTER the burst flush
		// (these are separate IIRs, not FA015807 spawn records) but still before the player starts moving.
		public async Task SendEntryNpcFollowups(INetworkClient client, List<WorldEntity> burstNpcs)
		{
			foreach (var npc in burstNpcs)
			{
				// #241: quest-giver marker from THIS viewer's quest state (! available / ? ready-to-turn-in / none).
				await _quests.SendQuestMarkerIfGiver(client, npc);

				// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose).
				if (npc.NpcTemplateId == TortageIntroService.CasildaTemplateId && client.Session.Character != null && !await _tortageIntro.IsCasildaFreed(client.Session.Character.Id))
					await _tortageIntro.SendCasildaPose(client, npc.Handle, true);

				// #363 dev nameplate override (dev-only, gated on .togglenpcgroup).
				if (_debugAreaLabels && npc.OverheadNameOverride is { } overrideName)
					await SendSetName(client, npc.Handle, overrideName);
			}
		}

		// Set the player's active weapon set explicitly (the .weaponset GM test-bootstrap forces a set; the live
		// SwitchWeaponsButton toggles via TogglePlayerWeaponSet). Persists + mirrors on the live entity; returns it.
		public async Task<WeaponSet> SetPlayerWeaponSet(INetworkClient client, WeaponSet set)
		{
			var player = _sim.PlayerWorldEntity(client);
			player?.ActiveWeaponSet = set;

			if (client.Session.Character is { } character)
			{
				character.ActiveWeaponSet = set;
				await _database.SaveCharacterWeaponSet(character.Id, set);
			}

			return set;
		}

		// #363 `.togglenpcgroup`: flip the dev spawn-area nameplate labels for the WHOLE server, then immediately
		// re-apply to every labelled NPC the caller already knows in their zone - pushing the area label when turning ON,
		// or restoring the plain creature name (which matches the client's LDB name) when turning OFF - so the toggle is
		// visible without a re-stream. NPCs that stream in later pick it up in SendWorldSpawn. Returns (nowOn, affected).
		public async Task<(bool On, int Affected)> ToggleAreaLabels(INetworkClient client)
		{
			_debugAreaLabels = !_debugAreaLabels;
			var player = _sim.PlayerWorldEntity(client);
			var affected = 0;
			if (player != null)
			{
				foreach (var npc in _sim.World.ZoneForEntity(player).Entities)
				{
					if (npc.OverheadNameOverride is not { } label || !player.Known.Contains(npc.Handle))
						continue;

					await SendSetName(client, npc.Handle, _debugAreaLabels
						? label
						: npc.Name ?? "");
					affected++;
				}
			}

			return (_debugAreaLabels, affected);
		}

		// #400 weapon-set switch: flip the player's active weapon LOADOUT (Primary<->Secondary), persist it so the drawn
		// set + the login-time gear-stat aggregation (which counts only the active set's weapons) survive relog, and
		// mirror it on the live entity. Returns the now-active set. The SwitchWeaponsButton action (0x2E) is the SET
		// switch, not a draw/sheathe (capture-confirmed: the acting player's newly-active weapon main slot alternates
		// 6<->20). No-op-safe: defaults Primary->Secondary if the player isn't in the world yet.
		public Task<WeaponSet> TogglePlayerWeaponSet(INetworkClient client)
		{
			var current = _sim.PlayerWorldEntity(client)?.ActiveWeaponSet ?? client.Session.Character?.ActiveWeaponSet ?? WeaponSet.Primary;
			return SetPlayerWeaponSet(client, current == WeaponSet.Primary
				? WeaponSet.Secondary
				: WeaponSet.Primary);
		}

		// Dispatches interest events. `move` (the mover's Move components) is required to forward Move events and is
		// null on entry (entry produces only Spawn/Despawn).
		// Dispatches an entity interest pass AND, for the moving/refreshing player, the static world-object pass. The two
		// are fused into one call on purpose: they are the same concern (bring this player's view in line with what is
		// near them) but obey different known-set rules, and when they were two separate calls at two call sites I wired
		// the object half into the quest-visibility path only - the two methods have identical three-line bodies, so a
		// textual edit hit the wrong one and objects never streamed as the player walked. Taking the player here means a
		// caller cannot dispatch one half without the other.
		private async Task DispatchInterest(IReadOnlyList<InterestEvent> events, byte[]? moveBody, INetworkClient? mover = null, WorldEntity? moverEntity = null)
		{
			if (mover != null && moverEntity != null)
				await StreamWorldObjects(mover, moverEntity);

			foreach (var ev in events)
			{
				var recipient = ev.Recipient.Client;
				if (recipient == null)
					continue;

				switch (ev.Type)
				{
					// A player is spawnable when it has a live entity (its record is built per send); everything else
					// still needs its prepared body.
					case InterestEventType.Spawn when ev.Subject.IsPlayer || ev.Subject.SpawnBody != null:
					{
						// Register a streamed-in NPC's handle -> name + template so clicking it routes to the right
						// conversation/shop by the stable template key (the same map the world-burst spawns use; players
						// are not conversation targets).
						if (ev.Subject is { IsPlayer: false, IsLoot: false })
						{
							recipient.Session.SpawnedNpcNamesByHandle[ev.Subject.Handle] = ev.Subject.Name;
							recipient.Session.SpawnedNpcTemplatesByHandle[ev.Subject.Handle] = ev.Subject.NpcTemplateId;
						}

						await SendWorldSpawn(recipient, ev.Subject);

						// A MOUNTED player's spawn record cannot show the mount: SpawnBody is the FA015807 captured at
						// their world entry, frozen before they ever mounted. The client renders the horse from the
						// Mount/MountSpeed stats, so send those straight after the spawn - otherwise walking up to a
						// mounted player shows them on foot, which is the half of #428 the live relay does not cover.
						if (ev.Subject is { IsPlayer: true, IsMounted: true, MountRideStat: > 0 })
						{
							await recipient.SendAsync(GameWire.BuildStatUpdate(recipient, new CharacterStatBlock
							{
								[CharacterStatId.Mount] = ev.Subject.MountRideStat,
								[CharacterStatId.MountSpeed] = 100
							}, ev.Subject.Handle));
						}

						// #241: when a quest-giver NPC enters view, set its marker from THIS viewer's quest state
						// (! available / ? ready-to-turn-in / none active). Recomputed per entry, so it transitions as
						// the player accepts/completes (e.g. Casilda shows ! then ? after the quest is done).
						if (ev.Subject is { IsPlayer: false, IsLoot: false })
							await _quests.SendQuestMarkerIfGiver(recipient, ev.Subject);
						// #716: a ROUTE walker's beat is issued here so the CLIENT walks it. This was removed once, for a
						// good reason that no longer holds: the client-walked route left entity.Position pinned at spawn,
						// so the server put the NPC's death-pose, loot bag and aggro where it STARTED rather than where the
						// player saw it. The model now walks the same polyline itself (WorldState.WalkRoute), at the route's
						// own captured speed, so it tracks the render without a packet per 6u - which is what made the beat
						// cost 19.3 MoveTask/s against retail's 3.09/s corpus maximum, and made the NPC run.
						//
						// ⚠️ Known gap: the body is anchored at the entity's SPAWN, so a viewer arriving MID-lap is told to
						// start the walk from the beginning. Retail's own form carries [duration][remaining] (535 of 759
						// captured multi-point patrol nodes do), which is presumably how it hands a late viewer the REST of
						// the beat; reproducing that is the rest of #716.
						// ⚠️ ARMED, not sent. Retail lets the client settle the spawn before handing it a timed walk (p50
						// 4.02s between the two over 2,519 captured NPCs; only 3% inside 0.1s). Sending both in the same
						// instant is what made a freshly-spawned NPC dash its first laps. The tick fires the beat to
						// everyone in range once the delay elapses - see WorldState.RoutePatrolDelaySeconds.
						if (ev.Subject is { IsPlayer: false, WalksRoute: true })
							ev.Subject.RoutePatrolDelayRemaining = WorldState.RoutePatrolDelaySeconds;
						// #267: a not-yet-freed Casilda streams in CHAINED (the captive struggle pose); once the player has
						// freed her she streams in normally (and follows). Per-viewer + per-instance (the beach is Solo).
						if (ev.Subject is { IsPlayer: false, NpcTemplateId: TortageIntroService.CasildaTemplateId } && recipient.Session.Character != null &&
						    !await _tortageIntro.IsCasildaFreed(recipient.Session.Character.Id))
							await _tortageIntro.SendCasildaPose(recipient, ev.Subject.Handle, true);
						break;
					}

					case InterestEventType.Move when moveBody != null:
					{
						await SendWorldMove(recipient, moveBody);
						break;
					}

					case InterestEventType.Despawn:
					{
						await SendWorldDespawn(recipient, _sim.World.ZoneForEntity(ev.Recipient).PlayfieldToken, ev.Subject.Handle);
						break;
					}
				}
			}
		}

		// Removes the subject from `recipient`'s view - the n3ToClientQuit despawn (N3ToClientQuitCodec). Stamped with the
		// recipient's INSTANCE playfield token (#268; ZoneForEntity(recipient).PlayfieldToken at the call site): the
		// burst's value for a replayed playfield (what the client learned at entry), else WorldState's minted fallback.
		// The token-is-0 guard is just defensive (ZoneFor always mints one).
		internal Task SendWorldDespawn(INetworkClient recipient, uint playfieldToken, uint subjectHandle, uint classWord = EntityClass.Character)
		{
			var token = playfieldToken;
			if (token == 0)
			{
				_logger.LogDebug("World: skip despawn {Subject:X8} for a viewer (playfield token unknown).", subjectHandle);
				return Task.CompletedTask;
			}

			return recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(N3ToClientQuitCodec.Build(subjectHandle, token, classWord))));
		}

		// `spawnAnimation` marks this ONE delivery as an entrance: the NPC (re)appeared just now, so the client should
		// play its spawn-in clip rather than fade it in standing (#803). Default false - it is true only on the
		// respawn path, which is the only creation event our world has that retail's own rule would also flag.
		internal async Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject, bool spawnAnimation = false)
		{
			var playfieldToken = _sim.World.ZoneForEntity(subject).PlayfieldToken;

			// A PLAYER's record is REBUILT from live state, not spliced out of a cached one (#429). The cached body is
			// the FA015807 from that player's own world ENTRY, so a viewer arriving later was handed a description of
			// them as they were at LOGIN - the mount was simply the first thing anyone noticed missing; equipment,
			// level and vanity have the same hole. Nothing here is replayed: PlayerCharacterIirs builds the message
			// from code (#258) and PlayerSpawnBuilder builds the record from scratch, so calling them per send costs
			// one equipment load and always describes the player as they are NOW.
			if (subject.IsPlayer)
			{
				var live = await BuildPlayerSpawnBody(subject, playfieldToken);
				if (live == null)
				{
					// No live session to describe. Loudly, not silently: a player we cannot build is a player nobody can
					// see, and the old code would have papered over it with a stale cached body.
					_logger.LogWarning("Spawn: cannot build a live record for player 0x{Handle:X8} ({Name}) - not sent.", subject.Handle, subject.Name);
					return;
				}

				await recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(live)));
			}
			else
			{
				await recipient.SendAsync(BuildWorldSpawnRecord(recipient, subject, playfieldToken, spawnAnimation));
			}

			// #363 dev nameplate: the client draws an NPC's floating name from its template_id LDB name and ignores the
			// spawn-body `Name`, so overriding the visible name needs a SetNameIIR (0xADCE0CDA). Sent right AFTER the
			// spawn (in TCP order) so the entity + its OverheadText exist before the name lands. Dev-only, gated on the
			// live `.togglenpcgroup` toggle, so it never touches retail parity.
			if (_debugAreaLabels && subject.OverheadNameOverride is { } overrideName)
				await SendSetName(recipient, subject.Handle, overrideName);

			await SendActiveBuffsOnStreamIn(recipient, subject);
		}

		// #677: an entity STREAMING INTO VIEW mid-buff. The observer arrived after the cast, so they have no apply
		// event to count from and see no icon - and re-sending the apply is exactly what retail's absolute expiry
		// stamp is FOR.
		//
		// ⭐ The shape is measured, not invented (`--report buff-on-spawn`). Over 17,717 captured spawn bodies and
		// 2,929 applies there is exactly ONE such record, and it is unique in three independent ways at once, which
		// is what makes one sighting enough to build from:
		//
		//   NecroL80_..._login_spellweaving +135.5s   to 0x0215E80C (a CHARACTER), spawned 0.00s earlier
		//   `Slayer of the Leviathus Spawn`  18,735,004 cs of 276,480,000 cs (6.8%)
		//   ApplyFrom = 0        <- a CHARACTER reference whose handle is zero: "a caster, unspecified"
		//   descriptor[3] = 0    <- no caster named, so no caster level
		//   CastingLevel = 80    <- the TARGET's level, which is the #658 decode falling out for free
		//
		// ⚠️ That zero-handle caster is NOT the same thing as the 11 casterless applies `ApplyNoCaster` was written
		// for. Those write a zero CLASS word and no reference at all (the Racer buff, #636); this one writes the
		// Character class and a zero instance behind it. The 54-byte data region is identical between the two forms -
		// which is why the payload factory is shared - but the carrier is not, so `ApplyFrom` is `0u` here and `null`
		// there. Passing `null` would build a record retail does not send.
		//
		// So it is a separate `MagicBuffIIR` right at the spawn, NOT a field of the spawn body - 0 of those 17,717
		// bodies carries a buff list. `MagicBuffDatas.ApplyNowNoCaster` is already that form (#636's Racer buff uses
		// it), so nothing new goes on the wire.
		//
		// ⚠️ It carries the REMAINING time, not the base duration, and NOT via the stamp: `ApplyNowNoCaster` computes
		// the stamp from the remaining time it is given, so both agree. Sending the full duration and letting the
		// stamp shorten it is the shape #601 was closed for proposing - it appears nowhere in the corpus.
		//
		// ⚠️ What one sighting does NOT settle is WHICH buffs qualify. 1 of 2,929 says it is not every buff on every
		// stream-in, and the one we have is a 32-day title buff. We send every TIMED buff the entity holds, which is
		// the narrowest thing we can do that is still useful - our own `ActiveBuffs` only ever holds self-buffs, so
		// this is another player's own buffs and nothing else. Open work tracked in Gitea #677.
		private static async Task SendActiveBuffsOnStreamIn(INetworkClient recipient, WorldEntity subject)
		{
			const uint UnspecifiedCaster = 0;

			if (subject.ActiveBuffs.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (wireId, buff) in subject.ActiveBuffs.ToArray())
			{
				// An untimed buff has no remaining time to state, and a nearly-expired one would arrive as a 0 the
				// client renders as a stuck icon. Both are skipped rather than rounded.
				if (buff.ExpiresAt is not { } expiresAt)
					continue;

				var remainingCs = (uint)Math.Max(0, (expiresAt - now).TotalSeconds * 100);
				if (remainingCs == 0)
					continue;

				var record = MagicBuffCodec.Build(subject.Handle, UnspecifiedCaster, wireId,
					MagicBuffDatas.ApplyNowNoCaster(MagicBuffIds.BaseOf(wireId), remainingCs, subject.Level));

				await recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType),
					GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
			}
		}

		// Sends the subject's FA015807 spawn to `recipient`, re-framed to the recipient and patched to the subject's
		// CURRENT position (its captured spawn body holds its entry position, which may be stale).
		// Builds one entity's FA015807 spawn record, framed for `recipient`: the stored SpawnBody with the LIVE position
		// patched in and the playfield-instance token re-stamped to `playfieldToken`. Shared by the interest send
		// (SendWorldSpawn) and the world-entry burst (#398, BuildEntryNpcBurst) so both produce byte-identical spawn
		// records - the burst is retail's shape (close NPCs on map enter) with our DB-constructed bodies.
		// One player's FA015807, built from their CURRENT state - the same two calls the world-entry path makes, so there
		// is one construction path and no cached blob to go stale. Returns null only when the player has no live session
		// left to describe (they disconnected mid-pass); anything else that would stop the build throws.
		//
		// The transform: POSITION comes from the live entity (the authoritative one the simulation moves), rotation from
		// the character row with the same degenerate-quaternion guard world entry uses - the entity carries Euler angles,
		// and the record wants the quaternion.
		private async Task<byte[]?> BuildPlayerSpawnBody(WorldEntity subject, uint playfieldToken)
		{
			if (subject.Client?.Session.Character is not { } character)
				return null;

			// The character's worn set. An EMPTY one is a naked character, not corruption: since #750 a worn item is an
			// item instance in container 101, so there is no equipment ROW whose absence could mean anything else. (This
			// used to throw on a missing row - which was right while the row was seeded at creation and its absence
			// really was corruption.)
			var worn = await _database.GetEquippedItems(character.Id);

			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			// The scaffold half of the record is built from the character's own rows (skills, feats, appearance model),
			// which the session character carries because every loader that produces one applies the same include set
			// (WithSessionNavigations). It did not, once: the session copy came back with Body + MaxStats only, so the
			// build failed - and that failure landed on the ENTERING player, hanging them on the loading screen over a
			// row missing from somebody ELSE's session object.
			// The vitals in the record are the subject's LIVE ceiling - the same value they see on their own sheet, so
			// a third party's health bar reads what its owner's does rather than a gear-less class curve (#580).
			return PlayerSpawnBuilder.BuildFromScratch(character.Id, playfieldToken, subject.Position, rotation,
				PlayerCharacterIirs.ForCharacter(character, worn, subject.Vitals?.ToMaxStats()!), character,
				await _database.GetMorphTargetRegistry());
		}

		private static Rpc BuildWorldSpawnRecord(INetworkClient recipient, WorldEntity subject, uint playfieldToken, bool spawnAnimation = false)
		{
			// ⭐ #803: a RESPAWN delivery carries the entrance-animation trigger; every other delivery of the same
			// entity does not. Stamped before the transform patch because the re-encode rebuilds the record, which
			// would discard a patch applied to the old bytes.
			// A freed escortee is described by her FREED body - the ordinary idle instead of the story pose, and walk
			// mode (#827). `Following` is the physical form of "freed": chained to the post, or walking behind someone.
			// Without this she kept the chained struggle after her rescue, because the pose the client holds comes from
			// the spawn record and no later BCC registration replaces it.
			var source = subject is { Following: true, FreedSpawnBody: not null }
				? subject.FreedSpawnBody
				: subject.SpawnBody;
			var body = spawnAnimation
				? NpcSpawnBuilder.WithSpawnAnimation(source)
				: (byte[])source.Clone();

			SpawnTransformPatch.WritePosition(body, subject.Position);

			// Re-stamp the playfield-instance token to the LIVE value at send time. The token baked into SpawnBody at
			// map-load (NpcSpawnBuilder.TryBuildLive) is the zone's DEFAULT token - but the client only registers, and
			// then polls (0x2020), a spawn whose playfield matches the one IT loaded at world entry, and that live token
			// isn't known until the player actually enters (docs/subsystems/mid-session-spawn-blocker.md part 3). Baking the
			// load-time token shipped every NPC with a stale playfield, so the client silently dropped them all and
			// never polled -> world entry never completed. Rewrite here, once the entering player's live token exists.
			SpawnTransformPatch.TryRewritePlayfield(body, playfieldToken);

			// The u32 carrier length belongs to the SENDER (#530 rule 2) - SpawnBody is the bare record.
			return new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body));
		}

		// #363 pushes a SetNameIIR (0xADCE0CDA) to `recipient` overriding entity `handle`'s floating nameplate with
		// `name` - the in-world form `SetNames.Nameplate` names (nameType 3 applies the name AND refreshes the
		// already-built OverheadText). Used by the spawn path and the .togglenpcgroup toggle.
		private static Task SendSetName(INetworkClient recipient, uint handle, string name)
		{
			return recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(SetNameCodec.Build(SetNames.Nameplate(handle, name)))));
		}

		// Frames a raw 0x2000 IIR body on the world conversation to one recipient (the generic single-recipient send the
		// dedicated builders share - e.g. the NPC MoveTask chase step).
		// Forwards a player's Move to `recipient` - the mover's own carrier bytes, only the frame receiver differs, which
		// is exactly what retail relays (see BroadcastPlayerMove). The body already names the moving entity's handle, so
		// there is nothing per-recipient to rebuild; sending it verbatim also carries whatever the flag1 layout implies
		// (the jump bit and its acceleration vector included) without this end having to understand it.
		private static Task SendWorldMove(INetworkClient recipient, byte[] moveBody)
		{
			return recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(moveBody)));
		}

		// Zone population - loading is ZonePopulationLoader's; these forward so the listener keeps one collaborator to
		// talk to about a zone (the same forwarding the listener does for its own services, CLAUDE.md).
		public Task EnsureZoneNpcs(uint mapId, uint instanceId, ConanClass forClass)
		{
			return _zones.EnsureZoneNpcs(mapId, instanceId, forClass);
		}

		public Task EnsureZoneWorldObjects(uint mapId, uint instanceId)
		{
			return _zones.EnsureZoneWorldObjects(mapId, instanceId);
		}

		public Task<string?> DescribeMissingClassImport(uint mapId, ConanClass forClass)
		{
			return _zones.DescribeMissingClassImport(mapId, forClass);
		}

		// Encounters and ambushes - the work is EncounterService's; these keep the port signatures.
		public Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			return _encounters.SwapEncounterLureForBoss(lure);
		}

		public Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			return _encounters.RestoreEncounterLure(boss);
		}

		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			_encounters.ScheduleQuestAmbush(client, ambush);
		}
	}
}
