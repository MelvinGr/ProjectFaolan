using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     A player dying, releasing, and coming back: the death sequence and its screen, the release timer, the
	///     self-resurrection offer a spell can arm, the choice of respawn point, and the discovery of new ones as the
	///     player walks past them.
	/// </summary>
	/// <remarks>
	///     It answers "what happens to a player between the killing blow and standing up again", as against
	///     <see cref="WorldSimulation" />'s "what is in the world and what is it doing this tick". The dependency runs
	///     one way: this class holds none of the simulation, only the two pieces of world state it reads - the player
	///     registry and the zone model - so the simulation calls in and nothing calls back. Its outbound port is bound
	///     at <see cref="WorldSimulation.Start" /> rather than injected, because everything it reaches is built on the
	///     world model.
	/// </remarks>
	public sealed class PlayerDeathService
	{
		/// <summary>How close a player must get to discover a resurrection point.</summary>
		/// <remarks>
		///     The captures can only put a floor under this: at the two discovery moments they carry the player was
		///     13.7 and 11.6 units out, but that is where the player had already got to when the server noticed, so it
		///     bounds the real radius from below rather than above. There is nothing to match byte for byte, and 30
		///     reads right in-client where 15 meant walking almost on top of a point. Contrast
		///     <c>RespawnPoint.RestorationLevel</c>, which the wire pins exactly.
		///     <para>
		///         It is not a per-point property: the RDB's <c>resurrectionpoint</c> element carries no radius. Its
		///         full attribute set is Guid, name, discoverable, Orientation, RestorationLevel, shady,
		///         FollowsHeightmap, HeightmapOffset, IsLocked, IsVisible, BoundingBoxColor and ShowBoundingBox, plus
		///         export metadata - see <c>RdbRespawnImporter</c>.
		///     </para>
		/// </remarks>
		private const float RespawnDiscoveryRange = 30f;

		private readonly ICharacterRepository _characters;
		private readonly IWorldRepository _worldData;
		private readonly ILogger _logger;
		private readonly PlayerRegistry _players;
		private readonly WorldState _world;

		private IWorldOutbound _output = null!;

		public PlayerDeathService(WorldState world, PlayerRegistry players, ICharacterRepository characters, IWorldRepository worldData, ILogger logger)
		{
			_world = world;
			_players = players;
			_characters = characters;
			_worldData = worldData;
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

		// Per-map resurrection points, cached alongside the per-character discovered sets to keep the discovery tick off
		// the database.
		private readonly ConcurrentDictionary<uint, RespawnPoint[]> _respawnPointsByMap = new ConcurrentDictionary<uint, RespawnPoint[]>();

		// The self-resurrection offers. An offer is armed while the player is alive - using a Self-Resurrection Potion
		// or an Elixir of Resurgence sends a resurrect request there and then - and answered when they die, with
		// ResurrectMe carrying the same spell id back. The item's own tooltip is the specification: "an option to
		// resurrect themselves at the location of their death... The potion must be consumed before the death occurs to
		// have an effect. The option to self-resurrect will persist until it is used."
		//
		// It is therefore per-character live state that has to outlive a death, keyed by character id rather than by
		// connection: a zone change tears the world connection down, and an offer that died with the connection would
		// be lost by walking through a door, which the tooltip rules out. It is not persisted, so it does not survive a
		// server restart - the same narrowing the consumable cooldowns have.
		private readonly ConcurrentDictionary<uint, SelfResurrectionOffer> _selfResurrection = [];

		public void ArmSelfResurrection(uint characterId, uint spellId, uint restorePercent)
		{
			_selfResurrection[characterId] = new SelfResurrectionOffer(spellId, restorePercent);
			_logger.LogInformation("Combat: {Character:X8} armed a self-resurrection (spell {Spell}, {Percent}%).", characterId, spellId, restorePercent);
		}

		/// <summary>
		///     The player declined the offer (<c>CancelResurrectionRequest</c>). The spell must match, or declining
		///     somebody else's offer would drop the potion this player paid for.
		/// </summary>
		public bool CancelSelfResurrection(uint characterId, uint spellId)
		{
			return _selfResurrection.TryGetValue(characterId, out var offer) && offer.SpellId == spellId && _selfResurrection.TryRemove(characterId, out _);
		}

		public bool HasSelfResurrection(uint characterId, uint spellId)
		{
			return _selfResurrection.TryGetValue(characterId, out var offer) && offer.SpellId == spellId;
		}

		/// <summary>
		///     The player took the offer (<c>ResurrectMe</c>): revive in place, at the position they died in, restoring
		///     the offer's percentage of all three pools.
		/// </summary>
		/// <remarks>
		///     The percentage is the spell's own - <c>AddSelfResurrectionSpell</c>'s field 188 - which is why a Potion
		///     restores 100 and a Minor Elixir 25.
		/// </remarks>
		/// <returns>
		///     False when there is no matching offer or the player is not dead, both of which are ordinary: this is
		///     also how a player-cast resurrect would arrive, and none of those are modelled.
		/// </returns>
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
				await _characters.SaveCharacterDeadState(character.Id, false);
			}

			// The same in-place revive the release screen takes: a BccIIR flipping bcc_state 29 to 1 closes the dead
			// window, then the vitals go out. No respawn point and no teleport - resurrecting where you fell is the
			// point of the item, and it is the one revive that needs no destination.
			await _output.SendRevive(client, entity.Handle, 0);
			await _output.PushVitals(client, vitals);

			_logger.LogInformation("Combat: {Player} self-resurrected on the spot (spell {Spell}, {Percent}% restored).", character.Name, spellId, offer.RestorePercent);
			return true;
		}

		private sealed record SelfResurrectionOffer(uint SpellId, uint RestorePercent);

		/// <summary>
		///     The player clicked a spot on the release screen (<c>RespawnMe</c>) and revives at that point.
		/// </summary>
		/// <param name="spawnPointId">
		///     The <c>RespawnPoint.Id</c> sent in the resurrection-spots list, echoed back. An id that does not resolve
		///     revives in place with a full restore.
		/// </param>
		/// <returns>False, doing nothing, when the player is not dead.</returns>
		public async Task<bool> RespawnAtChosenPoint(INetworkClient client, uint spawnPointId)
		{
			if (!_players.TryGetValue(client, out var entity) || client.Session.Character == null)
				return false;

			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			_playerRespawnRemaining.TryRemove(client, out _);
			var character = client.Session.Character;
			var point = await _worldData.GetRespawnPoint(spawnPointId); // the revive works without it; this drives the relocation

			// Revive at the point's RestorationLevel rather than always full: 344 of the 538 imported points restore
			// 10% and 157 restore 100%, so which spot the player picks is a real choice. An unresolved point restores
			// full.
			vitals.ReviveTo(point?.RestorationLevel ?? 0f);
			if (character.IsDead)
			{
				character.IsDead = false;
				await _characters.SaveCharacterDeadState(character.Id, false);
			}

			// Retail's RespawnMe revive, decoded from the beach death-and-resurrect capture at packets 1382-1387, is a
			// lightweight in-place sequence on the existing SimpleChar rather than a world re-entry: a BccIIR flips
			// bcc_state 29 to 1, closing the release window, and then a vitals update restores health. A full world
			// re-entry here rebuilds the dead spawn while IsDead lingers and leaves the client in the sticky
			// wave-respawn UI.
			await _output.SendRevive(client, entity.Handle, spawnPointId);
			await _output.PushVitals(client, vitals);
			_logger.LogInformation("Combat: {Player} resurrected (chosen spot {Spot}).", character.Name, point?.Name ?? $"0x{spawnPointId:X}");
			await RelocateToRespawnPoint(client, entity, character, point);
			return true;
		}

		/// <summary>
		///     The player's release-and-resurrect click. Retail respawn is click-driven, so this is the normal path.
		/// </summary>
		/// <returns>False, doing nothing, when the player is not dead.</returns>
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

		/// <summary>
		///     Discovers any resurrection point a living player has walked near, so they can respawn there later.
		/// </summary>
		/// <remarks>
		///     Run at the regen cadence of 1 Hz to keep it cheap; always-known points need no discovery. A new find is
		///     persisted and announced.
		/// </remarks>
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

					// The client's own discovery volume where there is one, the proximity radius otherwise. The
					// volume matters: the 11 shipped ones are 16x20 to 29x33 units and about 10 units tall, where
					// RespawnDiscoveryRange is a 60x60 box with no vertical bound at all, so the radius discovers a
					// point from roughly twice the distance retail requires and from any height. Only 11 of 554
					// points carry a volume, so the radius stays as the fallback.
					var inVolume = point.DiscoveryRegion != null
						? EnvironmentService.Contains(point.DiscoveryRegion, entity.Position)
						: Vector3.Distance(point.Position, entity.Position) <= RespawnDiscoveryRange;
					if (!inVolume)
						continue;

					await _characters.DiscoverRespawnPoint(character.Id, point.Id);
					discovered.Add(point.Id);
					newlyFound = true;

					// The banner the player sees. Only the point id crosses the wire - the client resolves the name
					// from LDB category 270 - so the RDB dev label never reaches the screen.
					//
					// No name key is sent. Retail carries a per-point one, but it is known for only the 2 points any
					// capture discovers, with no way to derive the other 536, and a keyless banner renders correctly
					// in-client (verified on Conall's Valley 3711341, a point no capture covers), which fits the LDB
					// strings being plaintext and the area-name banner's nested localized element carrying no key
					// either. The codec still takes one so the goldens can reproduce retail exactly.
					await _output.SendRespawnPointDiscoveredBanner(client, point.Id, null);
					_logger.LogInformation("World: {Player} discovered resurrection point {Point} ('{Name}').", character.Name, point.Id, point.Name);
				}

				if (!newlyFound)
					continue;

				// Retail sends the spots list again in the same frame as the banner, with the new point in it, so the
				// client's release screen has it the next time the player dies.
				//
				// In Hoxxling_mage-heraldofxotli_login_gameplay, frame 138 carries one spot (784236, the always-known
				// one) and frame 7862 carries two (784237 then 784236) at the moment the player walked near the
				// discoverable second - alive and mid-walk, so this does not open the release screen, which the client
				// shows only on the dead bcc_state. Both spots' positions match the imported respawn points exactly.
				var known = points.Where(p => p.AlwaysKnown || discovered.Contains(p.Id)).ToList();
				await _output.SendResurrectionSpots(client, entity.Position, known);
			}
		}

		/// <summary>
		///     A character's discovered respawn-point ids, loaded once and updated on discovery, to keep the discovery
		///     tick off the database.
		/// </summary>
		private async Task<HashSet<uint>> GetDiscoveredRespawnCached(uint characterId)
		{
			if (_discoveredRespawnByCharacter.TryGetValue(characterId, out var cached))
				return cached;

			var set = new HashSet<uint>(await _characters.GetDiscoveredRespawnPointIds(characterId));
			_discoveredRespawnByCharacter[characterId] = set;
			return set;
		}

		/// <summary>A map's resurrection points, cached on first use; they do not change within a run.</summary>
		private async Task<RespawnPoint[]> GetRespawnPointsCached(uint mapId)
		{
			if (_respawnPointsByMap.TryGetValue(mapId, out var cached))
				return cached;

			var points = await _worldData.GetRespawnPoints(mapId);
			_respawnPointsByMap[mapId] = points;
			return points;
		}

		/// <summary>
		///     Move a just-revived player to their resurrection point on the same map with no loading screen.
		/// </summary>
		/// <remarks>
		///     It updates the authoritative server position, sends the IntendedTeleport carrying the live playfield
		///     token so the client recognises the same playfield and does not reload, and then the teleport record that
		///     jumps the avatar there: the IntendedTeleport alone only confirms the playfield. A missing point or
		///     entity is a no-op, leaving the revive in place.
		/// </remarks>
		private async Task RelocateToRespawnPoint(INetworkClient client, WorldEntity? entity, Character character, RespawnPoint? point)
		{
			if (entity == null || point == null)
				return;

			var dest = new Vector3(point.PositionX, point.PositionY, point.PositionZ);
			entity.Position = dest;
			character.Position = dest;

			var liveToken = _world.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client, entity.Handle, character.MapId, liveToken);

			// The record's rotation slot is the resurrection point's quaternion verbatim: the four words after the
			// position normalise to 1.0 on all 128 captured records, so deriving a horizontal facing direction and
			// packing it into two of them yields a rotation of twice the intended yaw.
			var rot = new Quaternion(point.RotationX, point.RotationY, point.RotationZ, point.RotationW);
			await _output.SendReviveDestination(client, entity.Handle, character.MapId, dest, rot);
			_logger.LogInformation("Combat: {Player} relocated to '{Spot}' {Dest} (token 0x{Token:X}).", character.Name, point.Name, dest, liveToken);
		}

		/// <summary>The killer to name in the death's kill record.</summary>
		/// <remarks>
		///     The client's resurrect window goes into wave-respawn mode - the countdown - if and only if the killer
		///     resolves to the local player: its <c>ResurrectView</c> constructor looks the killer up and, finding a
		///     local non-proxy entity, sets its wave flag. Any other endpoint - a live mob, a now-dead corpse, even a
		///     handle the client has already despawned - makes it instant, as retail is, because the lookup simply
		///     fails to find the local player.
		///     <para>
		///         So the only rule that matters is never to attribute a death to the player itself, and the cause must
		///         not matter: a melee mob, a bleed whose caster has since died, a GM kill and an environmental death
		///         must all resurrect instantly. The last attacker is preferred even if it has since died or despawned,
		///         then the nearest hostile, and the player's own handle only when there was no attacker at all - the
		///         one case that still waves.
		///     </para>
		/// </remarks>
		private uint ResolveKiller(WorldEntity player, Vector3 deathPosition, uint mapId, uint selfHandle)
		{
			// The last thing that hit them wins, alive or not: a stale or dead handle is fine, because it is not the
			// local player, so the client takes the instant path. This is what keeps a bleed kill instant when the
			// caster is long dead.
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

		/// <summary>
		///     Revives a dead player through retail's in-place RespawnMe sequence: a BccIIR flipping bcc_state 29 to 1,
		///     closing the release window, and a vitals restore. Not a world re-entry.
		/// </summary>
		/// <remarks>
		///     The resolved point's id parameterises the BccIIR, and
		///     <see cref="RelocateToRespawnPoint" /> moves the player there afterwards.
		/// </remarks>
		private async Task RespawnPlayer(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			var entity = (_players.TryGetValue(client, out var playerEntity) ? playerEntity : null);

			if (character.IsDead)
			{
				character.IsDead = false;
				await _characters.SaveCharacterDeadState(character.Id, false);
			}

			// The point the player would respawn at - the closest known, else the map's always-known or first - used as
			// the revive id. Resolved before the vitals restore, because the point's RestorationLevel decides how much
			// comes back.
			var deathPos = entity?.Position ?? character.Position;
			var points = await GetRespawnPointsCached(character.MapId);
			var target = RespawnResolver.Resolve(deathPos, points, await GetDiscoveredRespawnCached(character.Id), character.IsCriminal);

			vitals.ReviveTo(target?.RestorationLevel ?? 0f); // with no point resolved, restore in full

			var handle = entity?.Handle ?? character.Id;

			// A player does not come back mounted. Retail unseats a rider who died on the respawn rather than at the
			// moment of death: in both captured cases the end-twin and the Mount=0 stat update follow the player's own
			// RespawnMe within two frames (chat-annotated-4 frames 9103 to 9108, and 15045 to 15047 for a lava death).
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
			// The release screen offers the point the revive would use, including the outlaw one for a criminal.
			var spot = RespawnResolver.Resolve(deathPosition, points, discovered, character.IsCriminal) ?? points[0];
			// The retail beach player-death sequence, in order (Stihetfv_..._login-die-resurect-beach packet 1179):
			// the death-pose MoveTask, which drops the avatar into the dead pose, then the kill record, then the
			// release screen, then the death BccIIR that enters the dead substate with its window and camera pan.
			//
			// The kill record's killer must not be the player itself, or the client's ResurrectView constructor goes
			// into wave-respawn mode - confirmed under gdb: it looks the killer up in its entity registry and, finding
			// a local non-proxy entity, sets the wave flag at +0x476. Resolving it to the actual mob makes the client
			// take the instant path, as retail does.
			//
			// The self-applied death-effect ApplySpell (0x0009F000) is deliberately not sent. It appears in the
			// AssassinL23 (map 2010) capture but not in the retail beach death, and that spell's client RDB effect sets
			// stat 0x546, the wave-respawn timer, to 15 - a second wave trigger. The dead state is driven entirely by
			// the BccIIR below, so the effect is redundant.
			var handle = character.Id;
			var killer = ResolveKiller(playerEntity, deathPosition, character.MapId, handle);
			await _output.SendDeathPose(client, handle, deathPosition);
			await _output.SendDeathTrigger(client, handle, killer);
			await _output.SendResurrectionSpots(client, deathPosition, [spot]);
			// The BccIIR that transitions the live SimpleChar into the dead state (bcc_state 29); retail sends it after
			// the release screen. It is what shows the release window for an in-session death, where the login spawn's
			// own bcc_state 29 only covers logging in while dead.
			await _output.SendDeathBcc(client, handle);
		}

		/// <summary>A dead player gets the release screen once and then waits for their RespawnMe click.</summary>
		/// <remarks>
		///     Retail respawn is purely click-driven - in AssassinL23_..._resurect the player sat dead for some 757
		///     packets before sending RespawnMe - so there is no auto-revive timer; a timer would pull the player back
		///     before the release screen could be used. The respawn itself is driven inbound, by
		///     <see cref="RespawnAtChosenPoint" /> or <see cref="TryRespawnNow" />.
		/// </remarks>
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

				// A client present in the dictionary has already been sent the release screen: send it exactly once,
				// then wait for RespawnMe.
				if (_playerRespawnRemaining.TryAdd(client, 0))
				{
					_logger.LogInformation("Combat: {Player} died; awaiting release (RespawnMe).", character.Name);
					// Death strips every buff and debuff. Without it a bleed keeps ticking through the release window
					// and resumes the instant the player revives to full, re-killing them. The client does not
					// self-expire an icon, so clearing the server state alone leaves the icon stuck on the bar counting
					// down after the resurrect; each one's end-twin removal has to go out too. That happens before the
					// dead-state BccIIR, and these are plain debuff removals, not the wave-triggering death-effect
					// spell SendDeathScreen deliberately skips.
					//
					// The death penalty is chosen from what the player is still carrying, so it is read before the
					// strip. The tiers stack - see DeathPenalty for the wire evidence: the Nth concurrent penalty is
					// the spell named "Death Penalty N", so the live buff set is the death counter.
					var penalty = DeathPenalty.NextTier(pair.Value.ActiveBuffs.Keys.Select(id => id >> 8));

					foreach (var buff in pair.Value.ActiveBuffs)
						await _output.SendBuffRemoval(client, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
					pair.Value.ActiveBuffs.Clear();
					await _output.SyncBuffStats(client); // #367: death strips the buffs, so it strips their stats too

					// After the strip, never before. Retail sends the new penalty's apply in the same batch as the
					// end-twins of everything else (chat-annotated-4, 20:24:08), so applying it first would just have the
					// strip above remove it again. A fourth death inside 30 minutes adds nothing - there is no
					// "Death Penalty 4" anywhere in the corpus - and NextTier returns 0 for that.
					if (penalty != 0)
					{
						await _output.ApplySelfBuff(client, penalty << 8);
						_logger.LogInformation("Combat: {Player} picked up death penalty spell {Penalty}.", character.Name, penalty);
					}
					// Persist the dead state so a logout while dead returns dead: the login snapshot then carries a
					// current health of 0 and the client shows the release window at world entry.
					character.IsDead = true;
					// The NPC death counter, which is what the character sheet shows. The separate slot the snapshot
					// sends as PvPTotalDeaths (0x292) is a career PvP total and must not move here: nothing on this
					// path can be a PvP death, because the server has no player-versus-player kill path at all.
					character.NpcDeaths++;
					await _characters.SaveCharacterDeadState(character.Id, true);
					await _characters.SetCharacterNpcKillCounts(character.Id, character.NpcKills, character.NpcDeaths);
					await SendDeathScreen(client, character, pair.Value, pair.Value.Position);
				}
			}
		}
	}
}
