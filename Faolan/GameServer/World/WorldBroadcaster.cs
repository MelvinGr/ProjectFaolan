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
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Who can see what, and the wire that follows from it. The interest pass decides which entities and world
	///     objects each player currently holds; this class turns that decision into packets - the spawn, the despawn,
	///     the move relay, the entry bursts - plus the "relay this record to everyone watching X" family every other
	///     broadcaster is built on.
	/// </summary>
	/// <remarks>
	///     <para>
	///         It reads the world model through <see cref="WorldSimulation" />, loads templates from the database, and
	///         frames every packet on the world conversation (<c>GameEndpoints.WorldSenderEndpoint</c>: sender is the
	///         world system 0x58, receiver the recipient's GameInterfaceType endpoint). The FA015807 body carries the
	///         subject's handle unchanged, so the recipient renders the subject as a separate entity.
	///     </para>
	///     <para>
	///         This class answers that one question and must not grow past it. What belongs elsewhere: what an NPC does
	///         (<see cref="NpcBroadcaster" />), the loot lifecycle (<see cref="LootBroadcaster" />), interacting with a
	///         world object (<see cref="WorldObjectService" />), the quest journal and its markers
	///         (<see cref="QuestBroadcaster" />) and the knockback (<c>Combat/KnockbackService</c>). The dependency on
	///         each is one-way: they call in here for a spawn or a despawn, nothing here calls out to them. The single
	///         deliberate exception is <c>QuestBroadcaster.SendQuestMarkerIfGiver</c>, which a streamed-in NPC needs.
	///     </para>
	/// </remarks>
	public sealed class WorldBroadcaster
	{

		// Dev visualization: when true, an NPC that resolves to an RDB spawn area has that area's label pushed as its
		// overhead nameplate (via SetNameIIR after spawn) so a manual creature-labelling pass can see which area it is in.
		// Off by default; toggled live by the `.togglenpcgroup` GM command (ToggleAreaLabels), with no config.
		//
		// Instance state, deliberately not static: this is a per-world-host toggle and this class is per-host, so a static
		// would be process-global - under `--role login --role realm=<id>` (or the all-in-one dev run) that means one GM's
		// debug nameplates following the other realm's players around.
		private bool _debugAreaLabels;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// _tortageIntro is constructed before the broadcaster (it needs only the sim + DB), so the interest stream can
		// call into it (Casilda's captive pose on stream-in) without a construction cycle.
		private readonly ComboService _combos;
		private readonly TortageIntroService _tortageIntro;

		private readonly WorldSimulation _sim;

		// Handles for every runtime-spawned entity (loot bags here, NPCs, pets and ambient members on NpcBroadcaster).
		// The range and why it matters are documented on the allocator itself. Shared, and injected rather than built
		// here: a second counter would hand out colliding handles.
		private readonly RuntimeHandleAllocator _handles;

		// Ambient spawn-group pools. Owns the pool state and the spawning; the streaming of what it stands up is
		// NpcBroadcaster.MaintainAmbientPools. Shared with it, so injected - it is needed here only to seed a zone.
		private readonly AmbientPoolService _ambient;

		// Decides the quest-marker byte, the forced quest-item drop and a player's held-quest sets. Shared with
		// QuestBroadcaster (it caches the quest-giver and goal-target tables, so a second instance would duplicate
		// them), which is why it is injected rather than built here.
		private readonly QuestMarkerResolver _questMarkers;

		// The quest wire. One-way: this class calls in (a streamed-in NPC needs its '!' or '?'); it holds nothing here.
		private readonly QuestBroadcaster _quests;

		// Fills a zone with its NPCs and world objects on first entry. Loading is state; the streaming that follows
		// belongs here. Forwarded to below so callers keep one collaborator for a zone.
		private readonly ZonePopulationLoader _zones;

		// Encounter lure/boss swaps and quest ambushes. Forwarded to below: all three are port methods
		// (IWorldOutbound / IQuestOutbound), so the signature has to stay here even though the work does not.
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

		/// <summary>
		///     Re-evaluates quest-gated NPC visibility for one player after their quest state changed (accept, advance or
		///     complete). Reloads their active-quest set, then runs an interest pass so newly-qualified gated NPCs spawn
		///     and no-longer-qualified ones despawn in place, with no player or NPC movement required.
		/// </summary>
		public async Task RefreshQuestVisibility(INetworkClient client)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			await _questMarkers.LoadActiveQuestIds(entity);
			var events = InterestManager.ResolveMove(_sim.World.ZoneForEntity(entity), entity, WorldSimulation.PlayerInterestRange, true);
			await DispatchInterest(events, null, client, entity);
		}

		/// <summary>
		///     Re-runs one player's interest pass with nothing having changed on their side. The world tick calls it on a
		///     slow cadence so that entities which moved themselves are streamed in and out.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Every other interest pass is driven by the player: BroadcastPlayerMove (they moved), BroadcastPlayerEntry
		///         (they arrived), RefreshQuestVisibility (their quests changed). Nothing is driven by the subject moving,
		///         and <c>BroadcastNpcMove</c> only sends to viewers who already hold the handle - so without this sweep, an
		///         NPC that walks out of range of a standing player freezes at its last rendered position and one that walks
		///         in stays invisible until the player next moves. A route walker travels its whole beat, up to 98
		///         waypoints in Old Tarantia, so this is the common case rather than a rare one.
		///     </para>
		///     <para>
		///         It cannot hang off the NPC's own move effect: a route walker emits no per-step effect at all - the beat
		///         is issued to the client once and walked there, with the server only advancing its model - so the entities
		///         most in need of this are exactly the ones with no move to hook.
		///     </para>
		///     <para>
		///         <c>moveBody: null</c> makes this Spawn/Despawn only; the movement itself rides BroadcastNpcMove or the
		///         issued patrol. The mover arguments are left null on purpose: they drive world-object streaming only, and
		///         a static object cannot walk into range.
		///     </para>
		///     <para>
		///         The gate on <c>WorldEntryDelivered</c> is required, not defensive. This is the one sender that reaches a
		///         client on nobody's schedule but the tick's, so it is the one that can reach a client that is still
		///         entering. World entry places the player in the zone - which is what makes them visible to this sweep -
		///         seconds before it flushes its own packets, and in that window the sweep would stream every in-range NPC
		///         to a client that has not yet been told which playfield it is in, stamped with the zone's minted fallback
		///         token instead of the map's. The client drops every one of them, and because the sweep seeds them into the
		///         player's Known set the entry burst then finds nothing in range and never corrects it: every NPC in the
		///         entry radius stays invisible for the whole session while remaining fully alive server-side.
		///         <c>Session.WorldEntered</c> is set well before the packets are built, so it is not the flag to gate on.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     The world objects within interest range at entry, as ready-to-send records in the position retail puts them:
		///     early in the entry stream, before the snapshot.
		/// </summary>
		/// <remarks>
		///     Mirrors BuildEntryNpcBurst in seeding the known set, so the per-move pass afterwards streams only the delta.
		///     No despawns are possible on a fresh entry because the known set starts empty.
		/// </remarks>
		public (List<Rpc> Records, int Total) BuildEntryWorldObjects(INetworkClient client, WorldEntity player, IReadOnlySet<uint> quests)
		{
			var zone = _sim.World.ZoneForEntity(player);
			var (spawn, _) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, quests);

			return ([.. spawn.Select(o => WorldObjectRpc(client, o, zone.PlayfieldToken))], zone.WorldObjects.Count);
		}

		/// <summary>
		///     Brings the player's rendered world objects in line with interest after a move: spawns what came into range,
		///     despawns what left.
		/// </summary>
		public async Task StreamWorldObjects(INetworkClient client, WorldEntity player)
		{
			var zone = _sim.World.ZoneForEntity(player);
			if (zone.WorldObjects.Count == 0)
				return;

			// On a move the entity's own set is current - BroadcastPlayerMove refreshes it immediately before this.
			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, player, WorldSimulation.PlayerInterestRange, player.Knowledge.ActiveQuestIds);

			foreach (var obj in spawn)
				await client.SendAsync(WorldObjectRpc(client, obj, zone.PlayfieldToken));

			foreach (var handle in despawn)
			{
				// Despawn with the object's own class - a Character-classed quit silently misses a world object.
				var classWord = zone.TryGetWorldObject(handle, out var gone)
					? WorldObjectColumns.TypeTagForClass(gone.SubOpcode)
					: WorldObjectColumns.TypeTagForClass(0);
				await SendWorldDespawn(client, zone.PlayfieldToken, handle, classWord);
			}
		}

		// One static world object's FullUpdate, built from its columns and stamped with the live playfield token. The
		// carrier's [u32 len] belongs to the sender, so it is written here rather than by the codec.
		private static Rpc WorldObjectRpc(INetworkClient client, LiveWorldObject obj, uint playfieldToken)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				// The door state comes from the live object, not the row, so a door this zone has open streams open. So
				// does a destructible's health: 142 of the 193 shipped harvest nodes were captured mid-harvest, so
				// replaying the captured word would render most of a zone's nodes empty while the server holds them full.
				s => s.WriteArrayUInt32Length(WorldObjectCodec.Build(obj.Row, obj.Handle, playfieldToken, obj.IsClosed, obj.Health)));
		}

		/// <summary>
		///     Relays a player's behavior (the BehaviorControlCenterIIR swing or cast animation) to every other player who
		///     can already see them, so the attack or cast renders on the other clients - the combat analogue of
		///     <see cref="BroadcastPlayerMove" />.
		/// </summary>
		/// <remarks>
		///     <paramref name="body" /> is the same BCC echo the actor's own client receives and already carries the actor's
		///     handle as instance; retail relays that exact body to nearby players, with only the frame receiver differing
		///     (the other-players-visible capture shows s2c BCC for other-player handles 0x1316xxx). Gated on the viewer's
		///     Known set so a still-loading client is not sent a behavior for a handle it has not built.
		/// </remarks>
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
				if (!viewer.Knowledge.Known.Contains(entity.Handle))
					continue; // only players who already see the actor (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     Despawns a world-owned entity (a killed NPC, an expired loot bag) for every nearby player that actually
		///     holds it in their Known set, removing the handle as it goes. Uses the same n3ToClientQuit packet as a player
		///     despawn.
		/// </summary>
		/// <remarks>
		///     The Known guard is an invariant, not an optimisation: an n3ToClientQuit for a handle the recipient was never
		///     sent crashes the client (<c>pcDynel == null</c>), so a still-loading player in range of an entity it never
		///     received must not get a despawn for it. Interest is the sole NPC spawn path, which makes Known the
		///     authoritative record of what has been sent to a client, and this range-based world-tick path has to consult
		///     it exactly as the interest path does.
		/// </remarks>
		public async Task BroadcastEntityDespawn(WorldEntity entity)
		{
			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client != null && viewer.Knowledge.Known.Remove(entity.Handle))
					await SendWorldDespawn(viewer.Client, _sim.World.ZoneForEntity(viewer).PlayfieldToken, entity.Handle);
			}
		}

		/// <summary>
		///     Fades an entity out for every player in range (n3ToClientFadeIIR_t) - the graceful despawn used for loot
		///     bags, consumed or expired. Loot bags carry the 0xC87A typetag.
		/// </summary>
		/// <remarks>
		///     Both messages are required: the fade alone leaves the bag on screen. Retail's captured completion is
		///     <c>n3ToClientFade -> UpdateSimpleDynel</c>, where the fade makes the object go away gracefully and the update
		///     states that it is spent. A client that keeps the object keeps everything hanging off it, including whatever
		///     it draws on a lootable.
		/// </remarks>
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

		/// <summary>
		///     The defender's shield rose changed - an NPC engaged or its shift timer moved a guard, or a player moved one
		///     of theirs. Sends the CombatShieldIIR to every viewer that has the defender confirmed.
		/// </summary>
		/// <remarks>
		///     Gated on Confirmed rather than Known, the same rule the heartbeat uses: the rose is drawn on the dynel, so a
		///     still-loading client has nothing to draw it over. The owner is skipped when the defender is a player, because
		///     the client sets stat 0xA5 locally before sending it up and an echo would only re-apply what it already drew.
		/// </remarks>
		public async Task BroadcastShieldRose(WorldEntity entity)
		{
			ArgumentNullException.ThrowIfNull(entity);
			var record = CombatShieldCodec.Build(entity.Handle, entity.ShieldDistribution);

			foreach (var viewer in _sim.World.ZoneForEntity(entity).PlayersInRange(entity.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Handle == entity.Handle)
					continue;

				if (viewer.Client != null && viewer.Knowledge.Confirmed.Contains(entity.Handle))
					await WorldIir.Send(viewer.Client, record);
			}
		}

		/// <summary>
		///     Sends records describing what an entity is doing to every viewer that can see it - the general form of the
		///     Broadcast* methods below, for a sequence that has no single name.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The rule it serves: a record addressed to a subject, describing world-visible state, goes to everyone who
		///         can see that subject. Measured rather than assumed - in the two-client capture
		///         (<c>login_2-clients_group-trade-friend</c>, the only one with two game connections) every subject of
		///         <c>MoveTaskIIR</c> (12/12), <c>BehaviorControlCenterIIR</c> (11/11), <c>LookAtIIR</c> (8/8),
		///         <c>StatUpdateIIR</c> (9/9) and <c>StopIIR</c> (4/4) appears on both clients' streams. What is genuinely
		///         per-player is the player's own state - <c>FullPlayerCharacterIIR</c> 0/2, <c>FeedbackIIR</c> 0/2,
		///         <c>UpdateInventoryIIR</c> 0/2 - plus a despawn, which is per viewer by interest
		///         (<c>N3ToClientQuitIIR</c> 2/44). <c>--report broadcast-check</c> re-measures it.
		///     </para>
		///     <para>
		///         Not every record of a broadcast type is a broadcast: a <c>BehaviorControlCenterIIR</c> carrying an ack is
		///         a reply to the actor's own request, and the two-client capture contains no combat so it cannot speak for
		///         those. Use this when the record says what an entity did, not when it answers what a client asked.
		///     </para>
		/// </remarks>
		public async Task BroadcastEntityRecords(WorldEntity subject, params byte[][] records)
		{
			ArgumentNullException.ThrowIfNull(subject);
			ArgumentNullException.ThrowIfNull(records);

			foreach (var viewer in _sim.World.ZoneForEntity(subject).PlayersInRange(subject.Position, WorldSimulation.PlayerInterestRange))
			{
				if (viewer.Client == null || !viewer.Knowledge.Known.Contains(subject.Handle))
					continue;

				foreach (var record in records)
					await WorldIir.Send(viewer.Client, record);
			}
		}

		/// <summary>
		///     On disconnect or logout: despawns the leaving player for everyone who could see them (n3ToClientQuit),
		///     before the entity is removed from the zone.
		/// </summary>
		/// <returns>
		///     The dispatch task, so the caller can fire and forget - the connection is going away. A no-op with no viewers.
		/// </returns>
		public Task BroadcastPlayerDespawn(WorldEntity leaver)
		{
			var events = InterestManager.ResolveRemove(_sim.World.ZoneForEntity(leaver), leaver);
			return DispatchInterest(events, null);
		}

		/// <summary>
		///     After world entry: spawns the entering player to nearby other players.
		/// </summary>
		/// <remarks>
		///     The entering player's own NPC set rides the entry burst (<see cref="BuildEntryNpcBurst" />) and is already in
		///     Known, so the interest pass streams no NPCs here and only the spawn-me-to-viewers half fires. Interest
		///     remains the sole path for the streamed delta as the player then walks.
		/// </remarks>
		public async Task BroadcastPlayerEntry(INetworkClient client)
		{
			// A player's record is built per send, so the precondition is a live entity, not a cached SpawnBody.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			var zone = _sim.World.ZoneForEntity(entity);

			// The burst NPCs are already in Known (BuildEntryNpcBurst), so includeNonPlayers here re-confirms them in
			// range without re-spawning; its real job at entry is spawning this player to nearby viewers.
			var events = InterestManager.ResolveMove(zone, entity, WorldSimulation.PlayerInterestRange, true, true);
			await DispatchInterest(events, null);
		}

		/// <summary>
		///     After a player's move: updates interest and broadcasts the resulting spawns and moves to nearby players.
		/// </summary>
		/// <param name="moveBody">
		///     The mover's own CharDCMove carrier verbatim, with only the env byte cleared, never a reconstruction. Retail
		///     relays a move byte-for-byte: across the corpus 9,438 of 9,932 c2s moves reappear s2c identical bar that byte,
		///     and in xxx_2clients_jumping all 35 do, to both clients. Relaying the bytes also keeps this end out of the
		///     flag-gated layout - how many rotation floats flag1 bit 4 implies, where a jump's acceleration vector sits
		///     (docs/re/chardcmove-grammar.md) - which a rebuild from parsed fields would have to know.
		/// </param>
		/// <param name="position">The accepted position, the same one the body carries. Drives interest only.</param>
		public async Task BroadcastPlayerMove(INetworkClient client, byte[] moveBody, Vector3 position)
		{
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return;

			// Advance the player's model position to where they actually are before computing interest; left frozen at the
			// entry point, only entities within range of that one spot would ever stream. This is also what makes other
			// players see this one at its live position.
			entity.Position = position;

			// includeNonPlayers: stream the zone's NPCs to this player as they move. The burst only renders the NPCs near
			// the entry point; this fills in the rest of the map. Burst NPCs are already in the player's Known set,
			// seeded at entry, so they are not re-spawned.
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
					entity.Knowledge.Known.Count);
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
				if (!viewer.Knowledge.Known.Contains(entity.Handle))
					continue; // only players who already see the caster (their client has the handle)

				await WorldIir.Send(viewer.Client, body);
			}
		}

		/// <summary>
		///     A player's stat change that other players must see. The mount is the clearest case: the ride buff is relayed
		///     by SendMagicBuffIir, but the client renders the horse from the StatUpd carrying Mount and MountSpeed, so
		///     without this the actor sits on a mount only on their own screen.
		/// </summary>
		/// <remarks>
		///     The stat body names the entity and the client argument only frames the receiver, so the same block is rebuilt
		///     per viewer, exactly as <see cref="BroadcastSpellToViewers" /> does with the ApplySpell body. Never relay for
		///     a handle the viewer's client has not built.
		/// </remarks>
		public async Task BroadcastStatsToViewers(INetworkClient actor, CharacterStatBlock stats)
		{
			var entity = _sim.PlayerWorldEntity(actor);
			if (entity == null)
				return;

			foreach (var viewer in _sim.World.ZoneForEntity(entity).Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == entity.Handle || viewer.Client == null)
					continue;
				if (!viewer.Knowledge.Known.Contains(entity.Handle))
					continue;

				await viewer.Client.SendAsync(GameWire.BuildStatUpdate(viewer.Client, stats, entity.Handle));
			}
		}

		/// <summary>
		///     Builds the in-range NPC spawns for the world-entry burst: the close NPCs retail ships before the client's
		///     first move (fr64-75 in the Hoxxling beach login capture), rather than dribbling them out as a post-load
		///     interest volley.
		/// </summary>
		/// <remarks>
		///     Each NPC is seeded into the entering player's Known set, so the interest layer
		///     (<see cref="BroadcastPlayerMove" />) streams only the delta as the player walks and never re-spawns or culls
		///     the initial set. Each handle is registered against its name and template for click routing, exactly as
		///     DispatchInterest does for a streamed spawn. The entering player is spawned to other viewers separately, by
		///     <see cref="BroadcastPlayerEntry" />.
		/// </remarks>
		/// <returns>
		///     The framed FA015807 records to append to the entry burst, plus the NPCs themselves - their quest markers and
		///     captive pose ride <see cref="SendEntryNpcFollowups" /> after the burst flush, because those are separate
		///     IIRs rather than spawn records.
		/// </returns>
		public async Task<(List<Rpc> Records, List<WorldEntity> Npcs)> BuildEntryNpcBurst(INetworkClient client)
		{
			var records = new List<Rpc>();
			var npcs = new List<WorldEntity>();

			// Gated on the player having a live entity, never on their SpawnBody: a player's record is rebuilt from live
			// state per send, so they never hold one, and the records built here are the NPCs' anyway.
			var entity = _sim.PlayerWorldEntity(client);
			if (entity == null)
				return (records, npcs);

			// Seed the player's active and completed quest set first so quest-gated NPCs are correctly included or
			// excluded in the burst from the very start.
			await _questMarkers.LoadActiveQuestIds(entity);

			var zone = _sim.World.ZoneForEntity(entity);
			var token = zone.PlayfieldToken;
			foreach (var npc in zone.EntitiesInRange(entity.Position, WorldSimulation.PlayerInterestRange, entity.Handle))
			{
				if (npc.IsPlayer || npc.IsLoot || npc.IsDead || npc.SpawnBody == null)
					continue;
				if (!npc.IsVisibleTo(entity))
					continue; // a quest-gated NPC the player does not qualify for yet - treat as not in range
				if (!entity.Knowledge.Known.Add(npc.Handle))
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

		/// <summary>
		///     Resolves the playfield token a map's world entry will stamp, for a cross-map teleport's destination announce.
		/// </summary>
		/// <remarks>
		///     The map's real playfield-instance token is threaded through map change, spawn, world objects and the snapshot
		///     so they agree by construction. It is sourced per map from <c>Map.PlayfieldToken</c>: the value the client
		///     scopes the character to when it loads that playfield, and the key it uses for per-area behaviour such as
		///     mount permission - a wrong token makes Old Tarantia read as the no-mount Tortage beach. A found map with an
		///     un-captured token (0) falls back to the standard playfield token, because 0 is the documented "no per-map
		///     override" state that most maps are in, not a lookup failure.
		/// </remarks>
		/// <exception cref="InvalidOperationException">The map id does not exist.</exception>
		public async Task<uint> ResolveMapPlayfieldToken(uint mapId)
		{
			var map = await _database.GetMap(mapId) ?? throw new InvalidOperationException($"ResolveMapPlayfieldToken: map {mapId} not found");
			return map.PlayfieldToken != 0
				? map.PlayfieldToken
				: WorldState.DefaultPlayfieldToken;
		}

		/// <summary>
		///     The per-NPC follow-ups for the entry-burst NPCs (<see cref="BuildEntryNpcBurst" />): the quest markers and
		///     the Casilda captive pose that DispatchInterest sends right after a streamed spawn.
		/// </summary>
		/// <remarks>
		///     Sent after the burst flush, because these are separate IIRs rather than FA015807 spawn records, but still
		///     before the player starts moving.
		/// </remarks>
		public async Task SendEntryNpcFollowups(INetworkClient client, List<WorldEntity> burstNpcs)
		{
			foreach (var npc in burstNpcs)
			{
				// The quest-giver marker from this viewer's quest state: '!' available, '?' ready to turn in, or none. The
				// marker context is cached per character.
				await _quests.SendQuestMarkerIfGiver(client, npc);

				// A not-yet-freed Casilda streams in chained, in the captive struggle pose.
				if (npc.NpcTemplateId == TortageIntroService.CasildaTemplateId && client.Session.Character != null && !await _tortageIntro.IsCasildaFreed(client.Session.Character.Id))
					await _tortageIntro.SendCasildaPose(client, npc.Handle, true);

				// Dev nameplate override, gated on .togglenpcgroup.
				if (_debugAreaLabels && npc.OverheadNameOverride is { } overrideName)
					await SendSetName(client, npc.Handle, overrideName);
			}
		}

		/// <summary>
		///     Sets the player's active weapon set explicitly: the .weaponset GM bootstrap forces a set, where the live
		///     SwitchWeaponsButton toggles through <see cref="TogglePlayerWeaponSet" />. Persists it and mirrors it on the
		///     live entity.
		/// </summary>
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

		/// <summary>
		///     `.togglenpcgroup`: flips the dev spawn-area nameplate labels for this world host, then re-applies them to
		///     every labelled NPC the caller already knows in their zone - pushing the area label when turning on, or
		///     restoring the plain creature name (which matches the client's LDB name) when turning off - so the toggle is
		///     visible without a re-stream. NPCs that stream in later pick it up in SendWorldSpawn.
		/// </summary>
		/// <returns>Whether labels are now on, and how many nameplates were re-applied.</returns>
		public async Task<(bool On, int Affected)> ToggleAreaLabels(INetworkClient client)
		{
			_debugAreaLabels = !_debugAreaLabels;
			var player = _sim.PlayerWorldEntity(client);
			var affected = 0;
			if (player != null)
			{
				foreach (var npc in _sim.World.ZoneForEntity(player).Entities)
				{
					if (npc.OverheadNameOverride is not { } label || !player.Knowledge.Known.Contains(npc.Handle))
						continue;

					await SendSetName(client, npc.Handle, _debugAreaLabels
						? label
						: npc.Name ?? "");
					affected++;
				}
			}

			return (_debugAreaLabels, affected);
		}

		/// <summary>
		///     Flips the player's active weapon loadout (Primary to Secondary and back), persists it so the drawn set and
		///     the login-time gear-stat aggregation - which counts only the active set's weapons - survive a relog, and
		///     mirrors it on the live entity.
		/// </summary>
		/// <remarks>
		///     The SwitchWeaponsButton action (0x2E) is the set switch, not a draw or sheathe: the acting player's
		///     newly-active weapon main slot alternates 6 and 20 in the captures. Defaults Primary to Secondary if the
		///     player is not in the world yet.
		/// </remarks>
		/// <returns>The now-active set.</returns>
		public Task<WeaponSet> TogglePlayerWeaponSet(INetworkClient client)
		{
			var current = _sim.PlayerWorldEntity(client)?.ActiveWeaponSet ?? client.Session.Character?.ActiveWeaponSet ?? WeaponSet.Primary;
			return SetPlayerWeaponSet(client, current == WeaponSet.Primary
				? WeaponSet.Secondary
				: WeaponSet.Primary);
		}

		/// <summary>
		///     Dispatches an entity interest pass and, for the moving or refreshing player, the static world-object pass.
		/// </summary>
		/// <remarks>
		///     The two passes are fused into one call on purpose. They are the same concern - bring this player's view in
		///     line with what is near them - but obey different known-set rules, and taking the player here is what stops a
		///     caller dispatching one half without the other.
		/// </remarks>
		/// <param name="moveBody">
		///     The mover's own move carrier, required to forward Move events. Null on entry, which produces only Spawn and
		///     Despawn events.
		/// </param>
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

						// A mounted player's spawn record cannot show the mount: the client renders the horse from the
						// Mount and MountSpeed stats, so those are sent straight after the spawn. Without them, walking
						// up to a mounted player shows them on foot.
						if (ev.Subject is { IsPlayer: true, IsMounted: true, MountRideStat: > 0 })
						{
							await recipient.SendAsync(GameWire.BuildStatUpdate(recipient, new CharacterStatBlock
							{
								[CharacterStatId.Mount] = ev.Subject.MountRideStat,
								[CharacterStatId.MountSpeed] = 100
							}, ev.Subject.Handle));
						}

						// When a quest-giver NPC enters view, set its marker from this viewer's quest state: '!'
						// available, '?' ready to turn in, or none active. Recomputed per entry, so it transitions as
						// the player accepts and completes.
						if (ev.Subject is { IsPlayer: false, IsLoot: false })
							await _quests.SendQuestMarkerIfGiver(recipient, ev.Subject);
						// A route walker's beat is armed here, not sent: retail lets the client settle the spawn before
						// handing it a timed walk (p50 4.02s between the two over 2,519 captured NPCs; only 3% inside
						// 0.1s), and sending both in the same instant makes a freshly-spawned NPC dash its first laps. The
						// tick fires the beat to everyone in range once the delay elapses - see
						// WorldState.RoutePatrolDelaySeconds.
						//
						// The client walks the beat, and the server's model walks the same polyline itself
						// (WorldState.WalkRoute) at the route's own captured speed, so the NPC's death pose, loot bag and
						// aggro land where the player saw it without costing a packet per 6 units.
						//
						// Known gap: the body is anchored at the entity's spawn, so a viewer arriving mid-lap is told to
						// start the walk from the beginning. Retail's own form carries [duration][remaining] - 535 of 759
						// captured multi-point patrol nodes do - which is presumably how it hands a late viewer the rest of
						// the beat.
						if (ev.Subject is { IsPlayer: false, Route.WalksRoute: true })
							ev.Subject.Route.RoutePatrolDelayRemaining = WorldState.RoutePatrolDelaySeconds;
						// A not-yet-freed Casilda streams in chained, in the captive struggle pose; once the player has
						// freed her she streams in normally and follows. Per viewer and per instance (the beach is Solo).
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

		/// <summary>
		///     Removes the subject from <paramref name="recipient" />'s view - the n3ToClientQuit despawn.
		/// </summary>
		/// <remarks>
		///     Stamped with the recipient's own instance playfield token (<c>ZoneForEntity(recipient).PlayfieldToken</c> at
		///     the call site): the burst's value for a replayed playfield, which is what the client learned at entry, else
		///     WorldState's minted fallback. The token-is-0 guard is defensive; ZoneFor always mints one.
		/// </remarks>
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

		/// <summary>
		///     Sends one entity's spawn record to <paramref name="recipient" />.
		/// </summary>
		/// <param name="spawnAnimation">
		///     Marks this one delivery as an entrance: the NPC appeared just now, so the client plays its spawn-in clip
		///     rather than fading it in standing. True only on the respawn path, the one creation event in this world that
		///     retail's own rule would also flag.
		/// </param>
		internal async Task SendWorldSpawn(INetworkClient recipient, WorldEntity subject, bool spawnAnimation = false)
		{
			var playfieldToken = _sim.World.ZoneForEntity(subject).PlayfieldToken;

			// A player's record is rebuilt from live state, never spliced out of a cached one: a cached body would be
			// the FA015807 from that player's own world entry, so a viewer arriving later would be handed a description
			// of them as they were at login - their mount, equipment, level and vanity all frozen. Nothing here is
			// replayed: PlayerCharacterIirs builds the message from code and PlayerSpawnBuilder builds the record from
			// scratch, so calling them per send costs one equipment load and always describes the player as they are now.
			if (subject.IsPlayer)
			{
				var live = await BuildPlayerSpawnBody(subject, playfieldToken);
				if (live == null)
				{
					// No live session to describe. Reported rather than dropped silently: a player that cannot be built is
					// a player nobody can see.
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

			// Dev nameplate: the client draws an NPC's floating name from its template_id LDB name and ignores the
			// spawn-body `Name`, so overriding the visible name needs a SetNameIIR (0xADCE0CDA). Sent right after the
			// spawn, in TCP order, so the entity and its OverheadText exist before the name lands. Gated on the live
			// `.togglenpcgroup` toggle, so it never touches retail parity.
			if (_debugAreaLabels && subject.OverheadNameOverride is { } overrideName)
				await SendSetName(recipient, subject.Handle, overrideName);

			await SendActiveBuffsOnStreamIn(recipient, subject);
		}

		/// <summary>
		///     Re-states an entity's timed buffs to an observer that streams it into view mid-buff. The observer arrived
		///     after the cast, so they have no apply event to count from and would see no icon; re-sending the apply is
		///     what retail's absolute expiry stamp is for.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The shape is measured (<c>--report buff-on-spawn</c>). Over 17,717 captured spawn bodies and 2,929
		///         applies there is exactly one such record, and it is unique in three independent ways at once, which is
		///         what makes one sighting enough to build from:
		///     </para>
		///     <code>
		///     NecroL80_..._login_spellweaving +135.5s   to 0x0215E80C (a character), spawned 0.00s earlier
		///     `Slayer of the Leviathus Spawn`  18,735,004 cs of 276,480,000 cs (6.8%)
		///     ApplyFrom = 0        - a character reference whose handle is zero: a caster, unspecified
		///     descriptor[3] = 0    - no caster named, so no caster level
		///     CastingLevel = 80    - the target's own level
		///     </code>
		///     <para>
		///         That zero-handle caster is not the same thing as the 11 casterless applies <c>ApplyNoCaster</c> covers.
		///         Those write a zero class word and no reference at all; this one writes the Character class and a zero
		///         instance behind it. The 54-byte data region is identical between the two forms, which is why the payload
		///         factory is shared, but the carrier is not - so <c>ApplyFrom</c> is <c>0u</c> here and <c>null</c> there,
		///         and passing <c>null</c> would build a record retail does not send.
		///     </para>
		///     <para>
		///         It is a separate <c>MagicBuffIIR</c> at the spawn, not a field of the spawn body: none of those 17,717
		///         bodies carries a buff list. <c>MagicBuffDatas.ApplyNowNoCaster</c> is already that form, so nothing new
		///         goes on the wire.
		///     </para>
		///     <para>
		///         It carries the remaining time rather than the base duration, and not via the stamp:
		///         <c>ApplyNowNoCaster</c> computes the stamp from the remaining time it is given, so the two agree.
		///         Sending the full duration and letting the stamp shorten it appears nowhere in the corpus.
		///     </para>
		///     <para>
		///         What one sighting does not settle is which buffs qualify: 1 of 2,929 says it is not every buff on every
		///         stream-in, and the one recorded is a 32-day title buff. Every timed buff the entity holds is sent, which
		///         is the narrowest useful rule - <c>ActiveBuffs</c> only ever holds self-buffs, so this is another player's
		///         own buffs and nothing else. Open work tracked in Gitea #677.
		///     </para>
		/// </remarks>
		private static async Task SendActiveBuffsOnStreamIn(INetworkClient recipient, WorldEntity subject)
		{
			const uint UnspecifiedCaster = 0;

			if (subject.ActiveBuffs.IsEmpty)
				return;

			var now = DateTimeOffset.UtcNow;
			foreach (var (wireId, buff) in subject.ActiveBuffs.ToArray())
			{
				// An untimed buff has no remaining time to state, and a nearly-expired one would arrive as a 0 that the
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

		/// <summary>
		///     One player's FA015807 spawn record, built from their current state with the same two calls the world-entry
		///     path makes, so there is one construction path and no cached blob to go stale.
		/// </summary>
		/// <remarks>
		///     Position comes from the live entity, the authoritative one the simulation moves; rotation comes from the
		///     character row with the same degenerate-quaternion guard world entry uses, because the entity carries Euler
		///     angles and the record wants the quaternion.
		/// </remarks>
		/// <returns>
		///     Null only when the player has no live session left to describe, having disconnected mid-pass. Anything else
		///     that would stop the build throws.
		/// </returns>
		private async Task<byte[]?> BuildPlayerSpawnBody(WorldEntity subject, uint playfieldToken)
		{
			if (subject.Client?.Session.Character is not { } character)
				return null;

			// The character's worn set. An empty one is a naked character, not corruption: a worn item is an item
			// instance in container 101, so there is no equipment row whose absence could mean anything else.
			var worn = await _database.GetEquippedItems(character.Id);

			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			// The scaffold half of the record is built from the character's own rows (skills, feats, appearance model),
			// which the session character carries because every loader that produces one applies the same include set
			// (WithSessionNavigations). A loader that omits it fails the build here, and the failure lands on the
			// entering player rather than on the character whose rows are missing.
			//
			// The vitals in the record are the subject's live ceiling - the same value they see on their own sheet - so a
			// third party's health bar reads what its owner's does rather than a gear-less class curve.
			return PlayerSpawnBuilder.BuildFromScratch(character.Id, playfieldToken, subject.Position, rotation,
				PlayerCharacterIirs.ForCharacter(character, worn, subject.Vitals?.ToMaxStats()!), character,
				await _database.GetMorphTargetRegistry());
		}

		private static Rpc BuildWorldSpawnRecord(INetworkClient recipient, WorldEntity subject, uint playfieldToken, bool spawnAnimation = false)
		{
			// A freed escortee is described by her freed body - the ordinary idle instead of the story pose, and walk
			// mode. `Following` is the physical form of "freed": chained to the post, or walking behind someone. The pose
			// the client holds comes from the spawn record and no later BCC registration replaces it, so without this she
			// keeps the chained struggle after her rescue.
			//
			// A respawn delivery carries the entrance-animation trigger; every other delivery of the same entity does not.
			// It is stamped before the transform patch because the re-encode rebuilds the record and would discard a patch
			// applied to the old bytes.
			var source = subject is { Following: true, FreedSpawnBody: not null }
				? subject.FreedSpawnBody
				: subject.SpawnBody;
			var body = spawnAnimation
				? NpcSpawnBuilder.WithSpawnAnimation(source)
				: (byte[])source.Clone();

			SpawnTransformPatch.WritePosition(body, subject.Position);

			// Re-stamp the playfield-instance token to the live value at send time. The token baked into SpawnBody at map
			// load (NpcSpawnBuilder.TryBuildLive) is the zone's default token, but the client only registers - and then
			// polls with 0x2020 - a spawn whose playfield matches the one it loaded at world entry, and that live token is
			// not known until the player actually enters (docs/subsystems/mid-session-spawn-blocker.md part 3). A spawn
			// carrying the load-time token is dropped silently and never polled, so world entry never completes.
			SpawnTransformPatch.TryRewritePlayfield(body, playfieldToken);

			// The u32 carrier length belongs to the sender; SpawnBody is the bare record.
			return new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body));
		}

		// Pushes a SetNameIIR (0xADCE0CDA) overriding the entity's floating nameplate - the in-world form
		// `SetNames.Nameplate` builds, where nameType 3 applies the name and refreshes the already-built OverheadText.
		// Used by the spawn path and the .togglenpcgroup toggle.
		private static Task SendSetName(INetworkClient recipient, uint handle, string name)
		{
			return recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(SetNameCodec.Build(SetNames.Nameplate(handle, name)))));
		}

		// Forwards a player's move to `recipient`: the mover's own carrier bytes, with only the frame receiver differing,
		// which is exactly what retail relays (see BroadcastPlayerMove). The body already names the moving entity's
		// handle, so there is nothing per-recipient to rebuild; sending it verbatim also carries whatever the flag1
		// layout implies, the jump bit and its acceleration vector included, without this end having to understand it.
		private static Task SendWorldMove(INetworkClient recipient, byte[] moveBody)
		{
			return recipient.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, recipient.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(moveBody)));
		}

		// Zone population - the loading is ZonePopulationLoader's; these forward so a caller keeps one collaborator to
		// talk to about a zone.
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
