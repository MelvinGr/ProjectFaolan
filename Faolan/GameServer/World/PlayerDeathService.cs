using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
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
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// A player DYING, releasing, and coming back (#900, cut out of WorldSimulation): the death sequence and its
	// screen, the release timer, the self-resurrection offer a spell can arm, the choice of respawn point, and the
	// discovery of new ones as the player walks past them.
	//
	// The question it answers is "what happens to a player between the killing blow and standing up again" - as
	// against `WorldSimulation`'s "what is in the world and what is it doing this tick". ⚠️ The dependency runs ONE
	// WAY: this class holds none of the simulation, only the two pieces of world state it reads (the player registry
	// and the zone model), so the simulation calls in and nothing calls back.
	//
	// Its outbound port is BOUND at Start rather than injected, for the same reason WorldSimulation's is: everything
	// it reaches is built ON the world model. See WorldSimulation._output.
	public sealed class PlayerDeathService
	{
		// How close a player must get to discover a resurrection point. The captures can only put a FLOOR under this:
		// at the two discovery moments they carry, the player was 13.7 and 11.6 units out, but that is where the
		// player had already GOT to when the server noticed - it bounds the real radius from below, not above. So
		// there is nothing to match byte-for-byte here and this is a feel call: 30 reads right in-client, where 15
		// meant walking almost on top of a point. Contrast RestorationLevel, which the wire pins exactly.
		//
		// This is NOT a per-point property: the RDB's resurrectionpoint element carries no radius (its full attribute
		// set is Guid/name/discoverable/Orientation/RestorationLevel/shady/FollowsHeightmap/HeightmapOffset/IsLocked/
		// IsVisible/BoundingBoxColor/ShowBoundingBox + Export* metadata - see RdbRespawnImporter).
		private const float RespawnDiscoveryRange = 30f;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly PlayerRegistry _players;
		private readonly WorldState _world;

		private IWorldOutbound _output = null!;

		public PlayerDeathService(WorldState world, PlayerRegistry players, IDatabaseRepository database, ILogger logger)
		{
			_world = world;
			_players = players;
			_database = database;
			_logger = logger;
		}

		/// <summary>Binds the outbound port. Called from <see cref="WorldSimulation.Start" />, once.</summary>
		public void BindOutbound(IWorldOutbound output)
		{
			_output = output;
		}

		/// <summary>Drops a player's pending release timer - they left the world, or were relocated out of death.</summary>
		public void ClearRespawnTimer(INetworkClient client)
		{
			_playerRespawnRemaining.TryRemove(client, out _);
		}

		private readonly ConcurrentDictionary<INetworkClient, double> _playerRespawnRemaining = new ConcurrentDictionary<INetworkClient, double>();

		private readonly ConcurrentDictionary<uint, HashSet<uint>> _discoveredRespawnByCharacter = new ConcurrentDictionary<uint, HashSet<uint>>();

		// Per-map resurrection points + per-character discovered sets, cached to keep the discovery tick off the DB.
		private readonly ConcurrentDictionary<uint, RespawnPoint[]> _respawnPointsByMap = new ConcurrentDictionary<uint, RespawnPoint[]>();

		// --- Self-resurrection offers (#890) ------------------------------------------------------------------
		//
		// An offer is ARMED while the player is alive - using a Self-Resurrection Potion or an Elixir of Resurgence
		// sends DispatchResurrectRequest there and then - and answered when they die, with ResurrectMe carrying the
		// same spell id back. The item's own tooltip is the specification: "an option to resurrect themselves at the
		// location of their death... The potion must be consumed before the death occurs to have an effect. The option
		// to self-resurrect will persist until it is used."
		//
		// So it is per-character live state that must outlive a DEATH, and it is keyed by character id rather than by
		// connection on purpose: a zone change tears the world connection down (ClientDisconnected -> RemovePlayer), and
		// an offer that died with the connection would be lost by walking through a door - which the tooltip's "will
		// persist until it is used" plainly rules out. It is not persisted, so it does not survive a SERVER restart;
		// that is the one narrowing, and the same one the consumable cooldowns next door have (#764).
		private readonly ConcurrentDictionary<uint, SelfResurrectionOffer> _selfResurrection = [];

		public void ArmSelfResurrection(uint characterId, uint spellId, uint restorePercent)
		{
			_selfResurrection[characterId] = new SelfResurrectionOffer(spellId, restorePercent);
			_logger.LogInformation("Combat: {Character:X8} armed a self-resurrection (spell {Spell}, {Percent}%).", characterId, spellId, restorePercent);
		}

		// c2s CancelResurrectionRequest(resurrectRequesterID, resurrectSpellID) - the player declined the offer. The
		// spell must match, or a decline of somebody ELSE's offer would drop the potion the player paid for.
		public bool CancelSelfResurrection(uint characterId, uint spellId)
		{
			return _selfResurrection.TryGetValue(characterId, out var offer) && offer.SpellId == spellId && _selfResurrection.TryRemove(characterId, out _);
		}

		public bool HasSelfResurrection(uint characterId, uint spellId)
		{
			return _selfResurrection.TryGetValue(characterId, out var offer) && offer.SpellId == spellId;
		}

		// c2s ResurrectMe(resurrectSpellID) - the player took the offer. Revives IN PLACE, at the position they died
		// in, restoring the offer's percentage of all three pools; the percentage is the spell's own
		// (`AddSelfResurrectionSpell`'s field 188), which is why a Potion restores 100 and a Minor Elixir 25.
		//
		// Returns false when there is no matching offer or the player is not dead - both of which are ordinary, since
		// this method is also how a PLAYER-cast resurrect would arrive and we model none of those yet.
		public async Task<bool> ResurrectFromOffer(INetworkClient client, uint spellId)
		{
			if (client.Session.Character is not { } character || !_selfResurrection.TryGetValue(character.Id, out var offer) || offer.SpellId != spellId)
				return false;

			if (!_players.TryGetValue(client, out var entity) || entity.Vitals is not { IsDead: true } vitals)
				return false;

			_selfResurrection.TryRemove(character.Id, out _);
			_playerRespawnRemaining.TryRemove(client, out _);

			vitals.ReviveTo(offer.RestorePercent);
			if (character.IsDead)
			{
				character.IsDead = false;
				await _database.SaveCharacterDeadState(character.Id, false);
			}

			// The same in-place revive the release screen takes (#214): a BccIIR flipping bcc_state 29 -> 1 closes the
			// dead window, then the vitals go out. No respawn point and no teleport - resurrecting where you fell is
			// the whole point of the item, and it is the one revive that genuinely needs no destination.
			await _output.SendRevive(client, entity.Handle, 0);
			await _output.PushVitals(client, vitals);

			_logger.LogInformation("Combat: {Player} self-resurrected on the spot (spell {Spell}, {Percent}% restored).", character.Name, spellId, offer.RestorePercent);
			return true;
		}

		private sealed record SelfResurrectionOffer(uint SpellId, uint RestorePercent);

		// The player clicked a spot on the release screen: RespawnMe(spawnPointId) (c2s 0x2024). spawnPointId is the
		// RespawnPoint.Id we sent in DispatchValidResurrectionSpots, echoed back. Revive at that point; if the id
		// doesn't resolve, fall back to the closest-known logic (RespawnPlayer). No-op if the player isn't dead.
		public async Task<bool> RespawnAtChosenPoint(INetworkClient client, uint spawnPointId)
		{
			if (!_players.TryGetValue(client, out var entity) || client.Session.Character == null)
				return false;

			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			_playerRespawnRemaining.TryRemove(client, out _);
			var character = client.Session.Character;
			var point = await _database.GetRespawnPoint(spawnPointId); // for logging/future teleport; revive works without it

			// Revive at the POINT's RestorationLevel, not always full (#375): 344 of the 538 imported points restore
			// 10% and 157 restore 100%, so which spot you pick is a real choice. An unresolved point restores full.
			vitals.ReviveTo(point?.RestorationLevel ?? 0f);
			if (character.IsDead)
			{
				character.IsDead = false;
				await _database.SaveCharacterDeadState(character.Id, false);
			}

			// Retail's RespawnMe revive (decoded from the beach death+resurrect capture, Stihetfv #1382-1387) is a
			// LIGHTWEIGHT IN-PLACE sequence on the existing SimpleChar, NOT a world re-entry: a BccIIR (0x642CD3D6)
			// flips bcc_state 29→1 (closes the release window), then a vitals update restores HP. Our old ReenterWorld
			// re-ran the whole world entry, which (a) rebuilt the dead spawn when IsDead lingered and (b) corrupted the
			// client into the sticky wave-respawn UI. We revive in place; honoring a FAR res point via IntendedTeleport
			// (0x10D27BC0) is the follow-up (the beach point is adjacent to the death spot).
			await _output.SendRevive(client, entity.Handle, spawnPointId);
			await _output.PushVitals(client, vitals);
			_logger.LogInformation("Combat: {Player} resurrected (chosen spot {Spot}).", character.Name, point?.Name ?? $"0x{spawnPointId:X}");
			await RelocateToRespawnPoint(client, entity, character, point);
			return true;
		}

		// The player's "release/resurrect" click (GameCharAgent.RespawnMe 0x2024) - retail respawn is click-driven,
		// the tick timer is only the fallback. Respawns immediately if the player is actually dead; returns false
		// (and does nothing) otherwise.
		public async Task<bool> TryRespawnNow(INetworkClient client)
		{
			if (!_players.TryGetValue(client, out var entity) || client.Session.Character == null)
				return false;

			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			_playerRespawnRemaining.TryRemove(client, out _);
			await RespawnPlayer(client, vitals);
			return true;
		}

		// Discovers any resurrection point a living player has walked near (so they can respawn there later). Run at the
		// regen cadence (1 Hz) to keep it cheap; always-known points need no discovery. Persists + notifies on a new find.
		public async Task DiscoverNearbyRespawnPoints()
		{
			foreach (var pair in _players)
			{
				var client = pair.Key;
				var character = client.Session.Character;

				var entity = pair.Value;
				var points = await GetRespawnPointsCached(entity.MapId);
				if (points.Length == 0)
					continue;

				var discovered = await GetDiscoveredRespawnCached(character.Id);
				var newlyFound = false;
				foreach (var point in points)
				{
					if (point.AlwaysKnown || discovered.Contains(point.Id))
						continue;

					// The client's own discovery VOLUME where we have one (#519), the proximity radius otherwise.
					// The volume is not a nicety: the 11 shipped ones are 16x20 to 29x33 units and ~10 units tall,
					// where RespawnDiscoveryRange is a 60x60 box with no vertical bound at all - so the radius
					// discovers a point from roughly twice the distance retail requires, and from any height. Only
					// 11 of 554 points carry one, so the radius stays as the fallback rather than going away.
					var inVolume = point.DiscoveryRegion != null
						? EnvironmentService.Contains(point.DiscoveryRegion, entity.Position)
						: Vector3.Distance(point.Position, entity.Position) <= RespawnDiscoveryRange;
					if (!inVolume)
						continue;

					await _database.DiscoverRespawnPoint(character.Id, point.Id);
					discovered.Add(point.Id);
					newlyFound = true;

					// The banner the player actually SEES. Only the point id crosses the wire - the client resolves
					// the name from LDB category 270 - so nothing renders the RDB dev label ("ResurrectionAIPoint2")
					// this used to print through a SendSystemMessage chat line that was never capture-derived.
					//
					// No name key: retail carries a per-point one, and we have it for only the 2 points any capture
					// discovers, with no way to derive the other 536. It turns out not to matter - a keyless banner
					// renders correctly in-client (verified on Conall's Valley 3711341, a point no capture covers),
					// which fits the LDB strings being plaintext and the area-name banner's nested <localized>
					// carrying no key either. The codec still takes one so the goldens can reproduce retail exactly.
					await _output.SendRespawnPointDiscoveredBanner(client, point.Id, null);
					_logger.LogInformation("World: {Player} discovered resurrection point {Point} ('{Name}').", character.Name, point.Id, point.Name);
				}

				if (!newlyFound)
					continue;

				// Retail sends the spots list again in the SAME frame as the banner, with the new point now in it, so
				// the client's release screen has it the next time the player dies (#372).
				//
				// Proven by Hoxxling_mage-heraldofxotli_login_gameplay: frame 138 carries ONE spot (0x0bf76c =
				// 784236, the AlwaysKnown one); frame 7862 carries TWO (784237 then 784236) at the moment the player
				// walked near the discoverable second - ALIVE and mid-walk, so this does not open the release screen
				// (the client only shows that on the dead bcc_state). Both spots' positions match our imported
				// RespawnPoints byte-for-byte.
				var known = points.Where(p => p.AlwaysKnown || discovered.Contains(p.Id)).ToList();
				await _output.SendResurrectionSpots(client, entity.Position, known);
			}
		}

		// A character's discovered respawn-point ids, cached (loaded once, updated on discovery) to keep the discovery
		// tick off the DB.
		private async Task<HashSet<uint>> GetDiscoveredRespawnCached(uint characterId)
		{
			if (_discoveredRespawnByCharacter.TryGetValue(characterId, out var cached))
				return cached;

			var set = new HashSet<uint>(await _database.GetDiscoveredRespawnPointIds(characterId));
			_discoveredRespawnByCharacter[characterId] = set;
			return set;
		}

		// A map's resurrection points, cached on first use (rarely changes within a run).
		private async Task<RespawnPoint[]> GetRespawnPointsCached(uint mapId)
		{
			if (_respawnPointsByMap.TryGetValue(mapId, out var cached))
				return cached;

			var points = await _database.GetRespawnPoints(mapId);
			_respawnPointsByMap[mapId] = points;
			return points;
		}

		// Move a just-revived player to their resurrection point on the SAME map with NO loading screen: update the
		// authoritative server position, send the IntendedTeleport (the LIVE playfield token so the client recognizes
		// the same playfield and doesn't reload), then the CharDCMove teleport that actually JUMPS the avatar there
		// (the IntendedTeleport alone only confirms the playfield - the move is the CharDCMove) (#214). No-op if the
		// point or entity is missing (revive in place).
		private async Task RelocateToRespawnPoint(INetworkClient client, WorldEntity? entity, Character character, RespawnPoint? point)
		{
			if (entity == null || point == null)
				return;

			var dest = new Vector3(point.PositionX, point.PositionY, point.PositionZ);
			entity.Position = dest;
			character.Position = dest;

			var liveToken = _world.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client, entity.Handle, character.MapId, liveToken);

			// The record's rotation slot is the res point's QUATERNION verbatim (#809): the four words after the
			// position normalise to 1.0 on all 128 captured records, so the horizontal facing DIRECTION this used to
			// derive and stuff into two of them was a rotation of twice the intended yaw.
			var rot = new Quaternion(point.RotationX, point.RotationY, point.RotationZ, point.RotationW);
			await _output.SendReviveDestination(client, entity.Handle, character.MapId, dest, rot);
			_logger.LogInformation("Combat: {Player} relocated to '{Spot}' {Dest} (token 0x{Token:X}).", character.Name, point.Name, dest, liveToken);
		}

		// On death: show the client the release screen (DispatchValidResurrectionSpots) so the player can pick where to
		// resurrect, instead of silently sitting at 0 HP until the auto-timer fires. We send the single closest known
		// point (the captures only ever carried one spot; the multi-spot framing isn't pinned). The client replies with
		// RespawnMe(spawnPointId), handled inbound. If the map has no point, skip (the auto-timer still revives).
		// Resolves the killer to put in the death's SimpleCharKilledBy record. The resurrect window (#214) goes into
		// WAVE-respawn mode (the dreaded countdown) IFF the killer resolves to the LOCAL player (the client's
		// ResurrectView ctor looks the killer up and, finding a local non-proxy entity = the player, sets its wave
		// flag); ANY other endpoint - a live mob, a now-dead corpse, even a handle the client has already despawned -
		// makes it INSTANT like retail (the lookup just fails to find the local player). So the ONLY rule that matters
		// is: never attribute a death to the player itself. The death CAUSE must not matter (#214/#274): a melee mob,
		// a DoT whose caster has since died, .kill, or an environmental death must all resurrect instantly. Prefer the
		// last attacker even if it has since died/despawned (still a non-self handle -> instant), else the nearest
		// hostile, else self only when there was literally no attacker at all (rare; the one case that still waves).
		private uint ResolveKiller(WorldEntity player, Vector3 deathPosition, uint mapId, uint selfHandle)
		{
			// The last thing that hit them wins - alive or not. A stale/dead handle is fine: it just isn't the local
			// player, so the client takes the instant path. This is what keeps a DoT-kill (caster long dead) instant.
			if (player.LastAttackerHandle is { } lastAttacker && lastAttacker != selfHandle)
				return lastAttacker;

			if (_world.TryGetZone(mapId, out var zone))
			{
				var nearest = zone
					.EntitiesInRange(deathPosition, WorldSimulation.PlayerInterestRange, selfHandle)
					.Where(e => e is { Hostile: true, IsDead: false })
					.OrderBy(e => Vector3.Distance(e.Position, deathPosition))
					.FirstOrDefault();
				if (nearest != null)
					return nearest.Handle;
			}

			return selfHandle;
		}

		// Revives a dead player via the LIGHTWEIGHT in-place revive (the retail RespawnMe sequence, #214): BccIIR
		// bcc_state 29→1 (closes the release window) + a vitals restore - NOT a world re-entry. The old re-entry path
		// re-rendered the whole world (and, while IsDead lingered, rebuilt the DEAD spawn / corrupted the client into
		// the sticky wave-respawn UI). The resolved point's id parameterizes the BccIIR; we revive in place (honoring
		// a far point's relocation via IntendedTeleport 0x10D27BC0 is the follow-up). Used by the click (TryRespawnNow)
		// and the tick fallback.
		private async Task RespawnPlayer(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			var entity = (_players.TryGetValue(client, out var playerEntity) ? playerEntity : null);

			if (character.IsDead)
			{
				character.IsDead = false;
				await _database.SaveCharacterDeadState(character.Id, false);
			}

			// The point the player would respawn at (closest known, else the map's AlwaysKnown/first) - used as the
			// BccIIR revive id. Revive in place (no teleport yet), so the character position is left at the death spot.
			// Resolved BEFORE the vitals restore: the point's RestorationLevel decides how much comes back (#375).
			var deathPos = entity?.Position ?? character.Position;
			var points = await GetRespawnPointsCached(character.MapId);
			var target = RespawnResolver.Resolve(deathPos, points, await GetDiscoveredRespawnCached(character.Id), character.IsCriminal);

			vitals.ReviveTo(target?.RestorationLevel ?? 0f); // no point resolved -> full

			var handle = entity?.Handle ?? character.Id;

			// You do not come back mounted. Retail unseats a rider who died on the RESPAWN, not at the moment of death:
			// in both captured cases the end-twin + StatUpd(Mount=0) follow the player's own RespawnMe within two frames
			// (chat-annotated-4 frames 9103->9108 and 15045->15047, the second one a lava death). #142.
			if (entity?.IsMounted == true || character.MountId != null)
				await _output.SendDismount(client);

			await _output.SendRevive(client, handle, target?.Id ?? 0);
			await _output.PushVitals(client, vitals);
			_logger.LogInformation("Combat: {Player} resurrected (point '{Point}', map {Map}).", character.Name, target?.Name ?? "none", character.MapId);
			await RelocateToRespawnPoint(client, entity, character, target);
		}

		private async Task SendDeathScreen(INetworkClient client, Character character, WorldEntity playerEntity, Vector3 deathPosition)
		{
			var points = await GetRespawnPointsCached(character.MapId);
			if (points.Length == 0)
				return;

			var discovered = await GetDiscoveredRespawnCached(character.Id);
			// The release screen offers the point the revive would use - including the outlaw one for a criminal (#517).
			var spot = RespawnResolver.Resolve(deathPosition, points, discovered, character.IsCriminal) ?? points[0];
			// #214: replay the retail BEACH player-death sequence (Stihetfv_..._login-die-resurect-beach pkt#1179) in
			// order: death-pose MoveTask (type 0x06) -> SimpleCharKilledBy -> 0x2024 release screen -> death BccIIR.
			// The death-pose MoveTask drops the avatar into the dead pose; the BccIIR (bcc_state 29) enters the Dead
			// substate (window + camera pan). The kill record's KILLER must NOT be the player itself or the client's
			// ResurrectView ctor goes into WAVE-respawn mode (#214, proven by live gdb: the ctor looks the killer up
			// in its entity registry and, finding a local non-proxy entity = the player, sets +0x476 -> wave). Resolve
			// it to the actual mob (ResolveKiller) so the client takes the instant RespawnMe (0x2024) path like retail.
			//
			// NB: we deliberately do NOT send the self-applied death-effect ApplySpell(0x0009F000) here. It came from
			// the AssassinL23 (map 2010) capture, but the retail BEACH death does NOT send it (proven by gdb 2026-06-20):
			// that spell's client RDB effect sets stat 0x546 (the wave-respawn timer) = 15, ANOTHER wave trigger. The
			// dead state is fully driven by the BccIIR below, so the effect is redundant; dropping it + the mob-killer
			// above are the two fixes that make the client take the instant-respawn path like retail beach.
			var handle = character.Id;
			var killer = ResolveKiller(playerEntity, deathPosition, character.MapId, handle);
			await _output.SendDeathPose(client, handle, deathPosition);
			await _output.SendDeathTrigger(client, handle, killer);
			await _output.SendResurrectionSpots(client, deathPosition, [spot]);
			// The BccIIR that transitions the live SimpleChar into the dead state (bcc_state 29) - retail sends it after
			// the release screen. This is what shows the release window for an IN-SESSION death (the login spawn's
			// bcc_state=29 only covers login-while-dead).
			await _output.SendDeathBcc(client, handle);
		}

		// A dead player gets the release screen ONCE and then waits for their RespawnMe (c2s 0x2024) click. Retail
		// respawn is purely click-driven (AssassinL23_..._resurect: the player sat dead ~757 packets before sending
		// RespawnMe), so there is NO auto-revive timer - the old 10s timer yanked the player back before the release
		// screen could be used (#214). Respawn is driven inbound by RespawnAtChosenPoint / TryRespawnNow.
		public async Task TickPlayerDeaths()
		{
			foreach (var pair in _players)
			{
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character, pair.Value.GearBonuses, pair.Value.ClassBase);
				if (!vitals.IsDead)
				{
					_playerRespawnRemaining.TryRemove(client, out _);
					continue;
				}

				// Presence in the dict = release screen already sent; send it exactly once, then wait for RespawnMe.
				if (_playerRespawnRemaining.TryAdd(client, 0))
				{
					_logger.LogInformation("Combat: {Player} died; awaiting release (RespawnMe).", character.Name);
					// Death strips all (de)buffs/DoTs. Without this a bleed keeps ticking through the release window and
					// resumes the instant the player revives to full - re-killing them and making resurrect look broken
					// (#274). The client does NOT self-expire an icon, so clearing the server state alone leaves the
					// debuff icon stuck on the bar (counting down) after resurrect - we must send each one's end-twin
					// removal so the client drops the icon too. Do it BEFORE the dead-state BccIIR (these are plain
					// debuff removals, not the wave-triggering death-effect spell SendDeathScreen deliberately skips).
					// The death penalty is chosen from what the player is STILL carrying, so read it before the strip.
					// The tiers STACK (DeathPenalty documents the wire evidence): the Nth concurrent penalty is the spell
					// named "Death Penalty N", so the live buff set is the death counter.
					var penalty = DeathPenalty.NextTier(pair.Value.ActiveBuffs.Keys.Select(id => id >> 8));

					foreach (var buff in pair.Value.ActiveBuffs)
						await _output.SendBuffRemoval(client, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
					pair.Value.ActiveBuffs.Clear();
					await _output.SyncBuffStats(client); // #367: death strips the buffs, so it strips their stats too

					// AFTER the strip, never before. Retail sends the new penalty's apply in the same batch as the
					// end-twins of everything else (chat-annotated-4, 20:24:08), so applying it first would just have the
					// strip above remove it again. A fourth death inside 30 minutes adds nothing - there is no
					// "Death Penalty 4" anywhere in the corpus - and NextTier returns 0 for that.
					if (penalty != 0)
					{
						await _output.ApplySelfBuff(client, penalty << 8);
						_logger.LogInformation("Combat: {Player} picked up death penalty spell {Penalty}.", character.Name, penalty);
					}
					// Persist the dead state (#214) so a logout-while-dead returns dead - the login snapshot then
					// carries current-health = 0 and the client shows the release window at world entry.
					character.IsDead = true;
					// The NPC counter (#808). The slot the snapshot sends as `PvPTotalDeaths` (0x292) is a PvP CAREER
					// total, and dying to a mob used to show against it. Nothing here can be a PvP death: the server
					// has no player-versus-player kill path at all.
					character.NpcDeaths++; // the sheet's death counter (#447)
					await _database.SaveCharacterDeadState(character.Id, true);
					await _database.SetCharacterNpcKillCounts(character.Id, character.NpcKills, character.NpcDeaths);
					await SendDeathScreen(client, character, pair.Value, pair.Value.Position);
				}
			}
		}
	}
}
