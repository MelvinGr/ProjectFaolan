using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// The live in-memory world (Gitea #404): owns the world model + the connected players'
	// authoritative transforms/vitals, and drives the 10 Hz simulation tick (NPC respawn/AI, vitals regen, player
	// death/respawn, resurrection-point discovery). Extracted out of GameServerListener so the simulation's state +
	// timing are no longer fused with the networking front-door; it reaches the wire through IWorldOutbound. Packet
	// CONSTRUCTION (spawn/move/loot broadcasts) and persistence still live on the listener for now (see the
	// architecture roadmap's later decomposition increments).
	public sealed class WorldSimulation
	{
		// How far a player perceives other players (world-entity interest radius; wider than say range).
		public const float PlayerInterestRange = 150f;

		// NPC auto-attack. The hostile-NPC swing now plays the decoded attack-ANIMATION ack (the "second BccAck" with
		// actor = NPC handle - ApplyNpcAttack -> IWorldOutbound.SendNpcAttackAnimation, docs/subsystems/combat.md) and then the
		// 0x96B8DC59 vitals move for the HP drop. CAVEAT (why this was once gated off): on a zone with many hostiles
		// (Tortage Beach has ~400) a player who keeps dying can hit a death-loop (re-enters the world every ~10s, never
		// finishes loading). Kept on to exercise real combat death + the death/release screen (#214).
		private const bool NpcAutoAttackEnabled = true;

		// #225: stream the continuous idle heartbeat (short-form BehaviorControlCenterIIR) for live NPCs so they keep
		// playing their idle animation instead of freezing. Confirmed-gated in the broadcaster (never hits a loading
		// client). A kill-switch for the in-client pass in case the cadence needs disabling rather than just tuning.
		private const bool NpcHeartbeatEnabled = true;

		// #363 ambient spawn-pool maintenance cadence. This interval doubles as the respawn delay - a killed pool member
		// is replaced on the next pass, so ~15s matches the DefaultRespawnDelaySeconds mobs use.
		private const double AmbientMaintainIntervalSeconds = 15.0;

		// Base game-loop cadence: 10 Hz. Continuous systems run every base tick with the real elapsed dt so they stay
		// smooth; slower rate-based systems run via a TickAccumulator at their own interval. The loop measures
		// wall-clock dt, not the nominal period, so a late tick doesn't desync the rates.
		private const double BaseTickSeconds = 0.1;

		// Slow-tick reporting: how often the loop may complain that it is behind, and how far under nominal counts as
		// behind. 0.9 leaves room for ordinary timer jitter (PeriodicTimer fires on the thread pool, so a tick landing
		// a few ms late is normal and is not a load problem).
		private const double SlowTickReportSeconds = 30.0;
		private const double SlowTickThreshold = 0.9;

		// How long after dealing/taking damage a player counts as "in combat" (HP + mana regen halted). Provisional.
		private const double CombatRegenPause = 6.0;

		// Applies damage/heal-over-time from active DoT debuffs (#274). Each tracked buff with a DotDelta bleeds (or
		// heals) the target's HealthCurrent on a fixed cadence, mirroring the captured StatDamageIIR cycle: the tick
		// moves the player's vitals by the delta and sends the byte-exact StatDamageIIR (+ a PushVitals for the bar).
		// The fallback DoT cadence, for a spell whose interval the captures never established. This was once a guess
		// ("bleeds tick roughly every couple of seconds") applied to EVERY DoT, until the interval was measured per
		// spell (#437) - see Spell.DotIntervalMs.
		//
		// It survived the measurement as the MEDIAN of the 53 intervals now measured, which is the most defensible
		// thing to guess for the 31 still unmeasured. Worth knowing that it is not the most COMMON value: the
		// measured set peaks at 1 s (16 spells) against 2 s (14), so the fallback is deliberately the middle of the
		// range rather than its mode, and it is still wrong for any individual spell that lands on it.
		private const double DotTickSeconds = 2.0;

		// Vitals regen cadence (rate-based; dt-scaled by the real elapsed time when it fires).
		private const double RegenIntervalSeconds = 1.0;

		// #348: how far OUTSIDE a captured area hull still counts as inside. The hull is built from the positions
		// retail was seen firing the banner at, so it can only cover ground a captured player walked - it is always
		// an under-estimate of the real region. The slack buys back some of that; the vertical one is looser because
		// a hull from a handful of samples barely captures an area's height range at all (Bridge of Vilerus I spans
		// 1 unit of Y across six sightings, which is the bridge deck, not the trigger's true band).
		private const float AreaBannerSlack = 8f;
		private const float AreaBannerVerticalSlack = 20f;

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

		// Local-say range in world units. Built-NPC tests showed a +4-unit offset renders "beside" the player, so
		// units are ~metres; ~50 is a generous "nearby" for /say. Tunable; same-map is the hard filter either way.
		private const float SayRangeUnits = 50f;
		private readonly ConcurrentDictionary<INetworkClient, double> _playerRespawnRemaining = new ConcurrentDictionary<INetworkClient, double>();

		// Cached DoT effect per base spell id (Spell.DotDelta/DotType): looked up once per spell, then reused so the
		// per-cast NPC path doesn't hit the DB. (delta 0 = not a DoT.)
		private readonly ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)> _dotEffects = new ConcurrentDictionary<uint, (int Delta, uint Type, uint IntervalMs)>();
		private readonly ConcurrentDictionary<uint, HashSet<uint>> _discoveredRespawnByCharacter = new ConcurrentDictionary<uint, HashSet<uint>>();

		// Players currently dead, with the seconds remaining until respawn (counted down every tick).
		private readonly ConcurrentDictionary<uint, MapArea[]> _mapAreas = new(); // #348: per-map sub-areas, read every tick

		// Per-map resurrection points + per-character discovered sets, cached to keep the discovery tick off the DB.
		private readonly ConcurrentDictionary<uint, RespawnPoint[]> _respawnPointsByMap = new ConcurrentDictionary<uint, RespawnPoint[]>();
		private readonly IChatServerDispatcher _agents;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		private readonly IWorldOutbound _output;

		private Task _tickTask = Task.CompletedTask;
		public ConcurrentDictionary<INetworkClient, WorldEntity> Players { get; } = new ConcurrentDictionary<INetworkClient, WorldEntity>();

		// Server-side terrain (RDB heightmap): ground-height snapping + terrain line-of-sight (#284). Reached by the
		// broadcaster (loot-bag Y) and the WorldState tick (aggro line-of-sight).
		public TerrainService? Terrain { get; }

		// The live world model + the connected players' authoritative world entities (transform + vitals).
		public WorldState World { get; }

		// How the tick loop is actually running (achieved rate + busy fraction). Read by the keep-alive Ping reply for
		// the client's dev "Server Framerate" overlay, and by the slow-tick warning below. See TickMetrics.
		public TickMetrics Metrics { get; } = new TickMetrics(1.0 / BaseTickSeconds);

		public WorldSimulation(IWorldOutbound output, IDatabaseRepository database, IChatServerDispatcher agents, ILogger logger, TerrainService? terrain = null)
		{
			_output = output;
			_database = database;
			_agents = agents;
			_logger = logger;
			Terrain = terrain;
			World = new WorldState(terrain);
		}

		// The player's live world entity for this connection (authoritative transform + vitals), or null if not in the
		// world yet.
		public WorldEntity? PlayerWorldEntity(INetworkClient client)
		{
			return Players.TryGetValue(client, out var entity)
				? entity
				: null;
		}

		// Registers (or relocates) the player in the live world after world entry, SEEDING the authoritative WorldEntity
		// transform + vitals from the persisted Character. After this the WorldEntity is the source of truth for live
		// position/vitals; the Character is only re-read on a fresh entry/teleport. Idempotent (GetOrAdd keeps the
		// existing entity, so vitals aren't reset across re-entry/teleport/respawn).
		public void RegisterPlayerInWorld(INetworkClient client, MapInstanceMode instanceMode = MapInstanceMode.Shared, IReadOnlyDictionary<CharacterStatId, int>? statBonuses = null,
			IReadOnlyDictionary<CharacterStatId, int>? classBase = null, IReadOnlyDictionary<uint, int>? spellBoosts = null,
			IReadOnlyDictionary<uint, uint>? spellScalingAttributes = null)
		{
			var character = client.Session.Character;

			var handle = character.Id;
			// The player's pre-existing entity (null on a fresh login) decides whether this is a re-entry that should
			// keep its current instance vs. a fresh placement that picks one from the map's mode (#268).
			Players.TryGetValue(client, out var existing);
			// #3: seed the max vitals with the equipped-gear bonus so the regen bar fills past the class base. Only the
			// fresh-entry seed uses it; a re-entry keeps its existing (already gear-aware) vitals via GetOrAdd.
			var entity = Players.GetOrAdd(client, _ => new WorldEntity(handle, character.MapId, true, character.Name ?? $"Character{handle}", client)
			{
				Vitals = EntityVitals.FromCharacter(character, statBonuses, classBase),
				// #401: cache the worn-gear physical armor + magical Protection for incoming-damage mitigation.
				ArmorRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Armor) ?? 0),
				Protection = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.Protection) ?? 0),
				// #399: cache the worn-gear Hit Rating (hit chance, not yet consumed) + the crit stats.
				HitRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.HitRating) ?? 0),
				CriticalRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalRating) ?? 0),
				CriticalDamageRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CriticalDamageRating) ?? 0),
				CombatRating = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.CombatRating) ?? 0),
				// #399: cache the worn-gear Magic Damage added flat to the player's spell hits.
				MagicDamage = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.MagicDamage) ?? 0),
				// Cache the worn-gear natural mana/stamina regen added onto the base regen tick.
				NaturalManaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalManaRegen) ?? 0),
				NaturalStaminaRegen = (uint)Math.Max(0, statBonuses?.GetValueOrDefault(CharacterStatId.NaturalStaminaRegen) ?? 0),
				// #400: seed the live drawn weapon set from the persisted value so a mid-session swap starts from the set
				// the character last had out (and the statBonuses above were aggregated for that same set).
				ActiveWeaponSet = character.ActiveWeaponSet,
				// #367: keep the gear bonuses themselves, not just their sum into the fields above - they are the
				// baseline a buff's stat delta is added to (and removed from) by BuffStatEffects.
				GearBonuses = statBonuses ?? new Dictionary<CharacterStatId, int>(),
				ClassBase = classBase ?? new Dictionary<CharacterStatId, int>(),
				// #590: the feat boosts for this character's spells, already fanned out from line to rank.
				SpellBoosts = spellBoosts ?? new Dictionary<uint, int>(),
				// #592: the caster's attribute scores and class, which is what the magic-damage scaling term reads,
				// plus the per-spell attribute overrides. Same inputs as the pools above (VitalAttributes.From), so
				// the two never disagree about what the character's Intelligence is.
				Attributes = VitalAttributes.From(classBase, statBonuses),
				CharacterClass = character.Class,
				SpellScalingAttributes = spellScalingAttributes ?? new Dictionary<uint, uint>()
			});
			// Re-apply the account-persisted GM toggles (#279): the live entity is fresh on each world entry (a map
			// change is a new world connection), so these would otherwise be lost on every zone. Only GMs ever carry them.
			if (client.Session.Account is { } account)
			{
				entity.GodMode = account.GmGodMode;
				entity.Invisible = account.GmInvisible;
			}

			var instanceId = World.ResolveEntryInstance(existing, character.MapId, instanceMode);
			// The DB rotation is a quaternion; the live sim/movement layer is Vector3 (Euler degrees) - bridge it.
			var zone = World.Place(entity, character.MapId, instanceId, character.Position, character.Rotation);
			var nearby = zone.PlayersInRange(character.Position, PlayerInterestRange, handle).Count();
			_logger.LogInformation("World: player {Name} (0x{Handle:X8}) entered map {Map} instance 0x{Instance:X8} at {Pos}; {Nearby} other player(s) in range.", entity.Name, handle, character.MapId,
				instanceId, character.Position, nearby);
		}

		// A same-MAP (same-playfield) teleport: relocate the player to `destination` on their CURRENT map with NO
		// loading screen, reusing the death-revive same-map path (IntendedTeleport with the LIVE playfield token +
		// n3Teleport). This is how a same-playfield zone-line door works - e.g. the Thirsty Dog Inn, whose UseObject
		// (door handle 0xBEB) resolves to IntendedTeleport destMap == the current map + an N3Teleport, with NO reconnect
		// (Hoxxling_..._gameplay2: door 0xBEB -> arrival (45, 158.05, 124) on map 4020, the player already on 4020).
		// Contrast DoTeleport, which cross-maps via a full 0x2002 handoff + reconnect (loading screen). The position is
		// persisted so it survives relog.
		public async Task RelocateSameMap(INetworkClient client, Vector3 destination)
		{
			var entity = PlayerWorldEntity(client);
			var character = client.Session.Character;
			if (entity == null || character == null)
				return;

			entity.Position = destination;
			character.Position = destination;
			await _database.SaveCharacterTransform(character.Id, destination, character.Rotation, character.MapId);

			var liveToken = World.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client!, entity.Handle, character.MapId, liveToken);
			await _output.SendReviveDestination(client!, entity.Handle, character.MapId, destination, 1f, 0f);
			_logger.LogInformation("World: {Player} same-map relocated to {Dest} on map {Map} (token 0x{Token:X}).", character.Name, destination, character.MapId, liveToken);
		}

		// Removes the player from the world model on disconnect. The caller broadcasts the despawn FIRST (while the
		// leaver is still in the zone so its viewers resolve), then calls this. Returns false if the player wasn't in
		// the world.
		public bool RemovePlayer(INetworkClient client, [MaybeNullWhen(false)] out WorldEntity entity)
		{
			if (!Players.TryRemove(client, out entity))
				return false;

			World.Remove(entity);
			_playerRespawnRemaining.TryRemove(client, out _);
			return true;
		}

		// The player clicked a spot on the release screen: RespawnMe(spawnPointId) (c2s 0x2024). spawnPointId is the
		// RespawnPoint.Id we sent in DispatchValidResurrectionSpots, echoed back. Revive at that point; if the id
		// doesn't resolve, fall back to the closest-known logic (RespawnPlayer). No-op if the player isn't dead.
		public async Task<bool> RespawnAtChosenPoint(INetworkClient client, uint spawnPointId)
		{
			if (!Players.TryGetValue(client, out var entity) || client.Session.Character == null)
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

		// The character ids that should hear a proximity "say" from `sender`: same-map players within SayRangeUnits of
		// the sender, plus the sender (you always hear yourself). Resolved against the live world model.
		public IReadOnlyCollection<uint> SayRecipients(INetworkClient sender)
		{
			var ids = new HashSet<uint>();
			var entity = PlayerWorldEntity(sender);
			if (entity == null)
			{
				// Not in the world model yet - fall back to just the sender so they still see their own message.
				if (sender.Session.Character != null)
					ids.Add(sender.Session.Character.Id);
				return ids;
			}

			ids.Add(entity.Handle);
			foreach (var near in World.ZoneForEntity(entity).PlayersInRange(entity.Position, SayRangeUnits, entity.Handle))
				ids.Add(near.Handle);

			return ids;
		}

		// Starts the simulation tick on the given lifetime token (owned by the listener so startup/shutdown stay in one
		// place). The task is held so StopAsync can await a clean exit.
		public void Start(CancellationToken cancellationToken)
		{
			_tickTask = RunTickLoop(cancellationToken);
		}

		// Awaits the tick loop's (cancelled) exit. The loop swallows its own OperationCanceledException, so this
		// completes normally once the lifetime token has been cancelled by the listener.
		public Task StopAsync()
		{
			return _tickTask;
		}

		// The player's "release/resurrect" click (GameCharAgent.RespawnMe 0x2024) - retail respawn is click-driven,
		// the tick timer is only the fallback. Respawns immediately if the player is actually dead; returns false
		// (and does nothing) otherwise.
		public async Task<bool> TryRespawnNow(INetworkClient client)
		{
			if (!Players.TryGetValue(client, out var entity) || client.Session.Character == null)
				return false;

			var vitals = entity.Vitals;
			if (vitals == null || !vitals.IsDead)
				return false;

			_playerRespawnRemaining.TryRemove(client, out _);
			await RespawnPlayer(client, vitals);
			return true;
		}

		// Applies an aggroed NPC's swing to the target player: play the decoded attack-animation ack
		// (SendNpcAttackAnimation), then reduce the player's live vitals and push a 0x96B8DC59 update so their HP bar
		// drops.
		private async Task ApplyNpcAttack(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			// #401: the NPC's swing is physical, so the player's worn-gear armor rating mitigates it. LEVEL and STRENGTH
			// are part of the client's own curve - it normalises armour against a per-level target and adds Strength at
			// 0.1/point above 10 - so both are passed rather than mitigating on the raw rating alone.
			// The curve wants the attribute at the ×10 WIRE scale (its `(attribute - 10) / 10` term converts back to
			// display units), so go through AttributeWireScore rather than the display score.
			var damage = CombatResolver.ApplyPhysicalMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level, npc.RangedAttacker), target.ArmorRating, character.Level,
				target.AttributeWireScore(CharacterStatId.Strength));
			// #621: the player's shield rose finally does something in PvE. The direction is the zone the MOB SWINGS
			// FROM - the same five the player's own direction abilities pick - not where the mob is standing. The
			// player defender takes the gentler Combat_PvP* column, matching CombatService's rule, and it is applied
			// AFTER armour, like the player-swing path.
			//
			// ⚠️ DRAWN ONCE, spent twice (#673). The swing's direction is on the wire - the hit result's type word IS
			// it - so the ack below has to state the same zone the mitigation charged for. Two draws made the packet
			// and the damage disagree on every swing. The MITIGATION only sees the guard arc (three), the WIRE gets
			// the full zone (five), which is why the draw is the zone and the arc falls out of it.
			var direction = CombatShieldRose.RollSwingDirection(Random.Shared);
			damage = (int)CombatShieldRose.ApplyArcMitigation((uint)damage, target.ShieldDistribution, CombatShieldRose.ArcOf(direction), playerVersusPlayer: true);
			// Play the NPC's swing animation (the decoded attack-animation ack) BEFORE the HP move lands, so the
			// hit reads as "swing -> damage". Visual only; the authoritative health change is the PushVitals below.
			// The swing ALWAYS plays - .godmode mobs still visibly attack, they just deal no damage.
			// #623: retail puts a combo on 427 of 1,504 captured NPC melee acks, so the mob combos on some swings and
			// swings plain on the rest. Rolled per swing rather than cycled, because the corpus shows no ordering -
			// a mob alternates its generic and class combos with plain hits in no pattern. 0 = plain.
			// #675: and when it does, a mob whose weapon answers its archetype swings a CLASS row about as often as
			// its generic one (the corpus splits 92/65 over the two wired weapons). Which rank is drawn from the rows
			// its level allows - retail's level-75 mobs still swing the rank-1 row, so there is no "highest wins".
			var comboId = npc.MeleeComboId != 0 && Random.Shared.NextDouble() < NpcWeapon.MeleeComboShare
				? npc.MeleeComboId
				: 0;
			if (comboId != 0 && npc.ClassComboIds.Length > 0 && Random.Shared.NextDouble() < NpcWeapon.ClassComboShare)
				comboId = npc.ClassComboIds[Random.Shared.Next(npc.ClassComboIds.Length)];

			await _output.SendNpcAttackAnimation(client!, npc.Handle, (uint)damage, ++npc.AttackSwingCounter, direction, comboId);

			if (target.GodMode) // .godmode: the NPC swings and aggros normally, but the hit deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}.", character.Name, npc.Name);
		}

		// Retail throws a rider off their mount under fire (#142): a mounted player who soaks enough damage gets the
		// ride buff's end-twin with no press of their own. The damage accumulates on the entity rather than testing a
		// single hit because the corpus rules a per-hit threshold out - a 200,496 hit leaves the rider seated while a
		// 10,944 one throws them - so the meter is the shape that survives the evidence even though its CEILING is a
		// placeholder (MountCatalog.ThrowOffDamage, #653).
		private async Task ThrowFromMountIfBattered(WorldEntity target, int damage)
		{
			if (!target.IsMounted || damage <= 0)
				return;

			target.MountDamageTaken += damage;
			if (target.MountDamageTaken < MountCatalog.ThrowOffDamage)
				return;

			_logger.LogInformation("Mount: {Player} was thrown from their mount after {Damage} damage.", target.Client?.Session.Character?.Name, target.MountDamageTaken);
			await _output.SendDismount(target.Client!);
		}

		// A caster NPC's tick action (#232): cast a spell at the target player (ApplySpell from the NPC handle) and
		// apply its damage, mirroring ApplyNpcAttack. The spell + effect payload come from the NPC's NpcSpell rows
		// (entity.Spells, loaded at zone load); a caster cycles through its spells. Damage is the provisional
		// melee-scaled value for now - the captured payload's magnitude isn't decoded, same interim as buffs.
		private async Task ApplyNpcCast(WorldEntity npc, WorldEntity target)
		{
			var client = target.Client;
			var character = client.Session.Character;
			if (character == null || npc.Spells.Count == 0)
				return;

			var vitals = target!.Vitals ??= EntityVitals.FromCharacter(character, target.GearBonuses, target.ClassBase);
			if (vitals.IsDead)
				return;

			var spell = npc.Spells[(int)(npc.AttackSwingCounter % (uint)npc.Spells.Count)];
			var debuffMs = ActiveBuff.DurationMsFromPayload(spell.Payload);

			// Retail re-applies an NPC debuff only AFTER the prior one expires (the combat capture's ~7s
			// apply->end->re-apply cycle); the swings in between are NORMAL attacks. Re-sending the ApplySpell every 2s
			// swing makes the client STACK the icon (piling up new copies at 0s with no countdown). So if the target
			// already carries this debuff, swing normally this tick instead - the debuff keeps counting down and is
			// re-cast once the tick expires it. (#274)
			if (debuffMs > 0 && target.ActiveBuffs.ContainsKey(spell.SpellId))
			{
				await ApplyNpcAttack(npc, target);
				return;
			}

			npc.AttackSwingCounter++;
			// #401: an NPC cast is magic damage, so the player's worn-gear Protection mitigates it (the magical mirror
			// of armor mitigating melee) - same level-normalised curve, against TWK_TargetPerLevelProtection.
			// NOT `npc.RangedAttacker`: #671's factor was measured on melee HITS (DamageResultIIR), and it is the
			// mob's ARROW that lands soft, not its spell. A bow-carrying caster would otherwise have its cast nerfed
			// for holding a bow.
			var damage = CombatResolver.ApplyMagicMitigation((int)CombatResolver.ResolveNpcAttackDamage(npc.Level, character.Level), target.Protection, character.Level,
				target.AttributeWireScore(CharacterStatId.Intelligence)); // the curve wants the ×10 wire scale
			await _output.SendNpcSpellCast(client!, npc.Handle, spell);

			// Track the NPC's cast as a timed DEBUFF on the player so it counts down and clears its icon at 0, like a
			// self-buff (#274). The duration is the cast payload's +8 field (centiseconds, see DurationMsFromPayload).
			// Only a real effect (duration > 0) is tracked - an instant damage spell carries none and leaves no icon.
			// CasterHandle = this NPC so the tick's removal twin mirrors the apply (applyTo=player, applyFrom=NPC).
			if (debuffMs > 0)
			{
				// Attach the DoT effect (Spell.DotDelta, captured StatDamageIIR) so the tick bleeds the player's health
				// while the debuff is active - Rend -8/tick etc. Looked up by base spell id (wire id >> 8). #274
				var (dotDelta, dotType, dotIntervalMs) = await DotEffect(spell.SpellId >> 8);
				target.ActiveBuffs[spell.SpellId] = new ActiveBuff(spell.SpellId, npc.Handle, DateTimeOffset.UtcNow, debuffMs / 1000f)
				{
					DotDelta = dotDelta,
					DotType = dotType,
					DotIntervalMs = dotIntervalMs,
					LastDotTick = DateTimeOffset.UtcNow
				};
			}

			if (target.GodMode) // .godmode: the NPC still casts, but the spell deals no damage
				return;

			target.LastAttackerHandle = npc.Handle; // attribute the death to this mob, not self (instant respawn, #214)
			target.LastCombatAt = DateTimeOffset.UtcNow; // taking damage -> in combat (halts HP/mana regen)
			vitals.ApplyHealthDelta(-damage);
			await _output.SendDamageNumber(client!, target.Handle, npc.Handle, (uint)damage); // floating hit number (#274)
			await _output.PushVitals(client!, vitals);
			await ThrowFromMountIfBattered(target, damage);

			if (vitals.IsDead)
				_logger.LogInformation("Combat: {Player} was killed by {NpcTemplate}'s spell.", character.Name, npc.Name);
		}

		// Discovers any resurrection point a living player has walked near (so they can respawn there later). Run at the
		// regen cadence (1 Hz) to keep it cheap; always-known points need no discovery. Persists + notifies on a new find.
		private async Task DiscoverNearbyRespawnPoints()
		{
			foreach (var pair in Players)
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

		private async Task<(int Delta, uint Type, uint IntervalMs)> DotEffect(uint baseSpellId)
		{
			if (_dotEffects.TryGetValue(baseSpellId, out var cached))
				return cached;

			var spell = await _database.GetSpell(baseSpellId);
			var effect = (spell?.DotDelta ?? 0, spell?.DotType ?? 0, spell?.DotIntervalMs ?? 0);
			_dotEffects[baseSpellId] = effect;
			return effect;
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

		private async Task<MapArea[]> GetMapAreasCached(uint mapId)
		{
			if (_mapAreas.TryGetValue(mapId, out var cached))
				return cached;

			var areas = await _database.GetMapAreas(mapId);
			_mapAreas[mapId] = areas;
			return areas;
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

		// Regenerates each connected player's vitals by `elapsedSeconds` worth and pushes a 0x96B8DC59 update only when
		// a pool actually rose (a player at full HP/stamina/mana costs nothing). The rates are ATTRIBUTE-driven and
		// corpus-verified (RegenRates, #442) - they replaced flat fractions of the pool max, which retail does not use.
		private async Task RegenerateConnectedPlayers(double elapsedSeconds)
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = pair.Value.Vitals ??= EntityVitals.FromCharacter(character, pair.Value.GearBonuses, pair.Value.ClassBase);
				if (vitals.IsDead)
					continue; // corpses don't regen - the respawn timer revives them

				// HP + mana regen is OUT-OF-COMBAT only (retail's OOCHealth/ManaRegeneration): halt them while the player
				// has dealt/taken damage within CombatRegenPause. Stamina (the action resource) keeps regenerating.
				var entity = pair.Value;
				var inCombat = (DateTimeOffset.UtcNow - entity.LastCombatAt).TotalSeconds < CombatRegenPause;
				uint beforeH = vitals.Health, beforeM = vitals.Mana, beforeS = vitals.Stamina;

				// Retail's rates, from the character's attribute scores (RegenRates: the tweak table's own 0.15/0.38
				// modifiers, each confirmed against a captured RegenIIR delta at ×100 precision). Stamina keeps its
				// worn-gear Natural Stamina Regen on top - that item stat (0x385) is additive to the OOC rate.
				uint strength = entity.AttributeScore(CharacterStatId.Strength),
					constitution = entity.AttributeScore(CharacterStatId.Constitution),
					dexterity = entity.AttributeScore(CharacterStatId.Dexterity),
					intelligence = entity.AttributeScore(CharacterStatId.Intelligence),
					wisdom = entity.AttributeScore(CharacterStatId.Wisdom);
				var changed = vitals.Regenerate(inCombat
						? 0u
						: RegenRates.Gain(RegenRates.HealthPerSecond(constitution, strength), elapsedSeconds),
					RegenRates.Gain(RegenRates.StaminaPerSecond(constitution, strength, dexterity), elapsedSeconds) + (uint)(entity.NaturalStaminaRegen * elapsedSeconds), inCombat
						? 0u
						: RegenRates.Gain(RegenRates.ManaPerSecond(intelligence, wisdom), elapsedSeconds) + (uint)(entity.NaturalManaRegen * elapsedSeconds));
				if (!changed)
					continue;

				// Retail's compact RegenIIR delta (0x28D0AC99): the per-tick rise per pool, ×100 to the wire vitals
				// scale. The client's apply (FUN_0079dd10) reads stat -> adds the delta -> writes it back through the
				// SAME setter StatUpdateIIR's apply uses (entity+0x10c vtable +0xc), so this is retail's own channel
				// for moving the bars without a full stat block.
				await _output.SendRegen(client, entity.Handle, (int)(vitals.Health - beforeH) * 100, (int)(vitals.Mana - beforeM) * 100, (int)(vitals.Stamina - beforeS) * 100);
				// ...and then the AUTHORITATIVE 0x96B8DC59 block, which is what actually makes regen visible (#2, the
				// in-client-verified channel). Sending the delta ALONE was a regression: 2026-07-22 replaced this push
				// with the RegenIIR and regen stopped ticking in-client, even though the packet is byte-identical in
				// shape to all 1502 captured retail RegenIIRs (len 29, env 0, class 0xC350, three BE i32). Order
				// matters: the delta goes FIRST so the absolute value lands last and wins - the client can never
				// double-apply, and any client/server drift is corrected every tick. Retail is lighter (it pins a pool
				// with a StatUpdate only on the tick that tops it off - Hoxxling_mage-heraldofxotli_login_gameplay
				// 18:49:49.912 sends REGEN(2060,1992,882) + a StatUpdate setting stamina/mana to their max); thinning
				// ours down to that shape needs the delta channel proven in-client first (Gitea #2).
				await _output.PushVitals(client, vitals);
			}
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

			var liveToken = World.ZoneForEntity(entity).PlayfieldToken;
			await _output.SendReviveTeleport(client, entity.Handle, character.MapId, liveToken);

			var rot = new Quaternion(point.RotationX, point.RotationY, point.RotationZ, point.RotationW);
			var fwd = Vector3.Transform(Vector3.UnitZ, rot);
			var hlen = MathF.Sqrt(fwd.X * fwd.X + fwd.Z * fwd.Z);
			var (facingX, facingZ) = hlen > 1e-4f
				? (fwd.X / hlen, fwd.Z / hlen)
				: (1f, 0f);
			await _output.SendReviveDestination(client, entity.Handle, character.MapId, dest, facingX, facingZ);
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

			if (World.TryGetZone(mapId, out var zone))
			{
				var nearest = zone
					.EntitiesInRange(deathPosition, PlayerInterestRange, selfHandle)
					.Where(e => e is { Hostile: true, IsDead: false, Vitals: not null })
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
			var entity = PlayerWorldEntity(client);

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

		// The server game loop (Gitea #404): a 10 Hz base tick that advances continuous systems
		// every tick with the REAL elapsed dt (NPC respawn/AI, player death timers, boundary-crossing tests) and runs
		// rate-based systems (vitals regen, ambient pools) via a TickAccumulator at their own interval, dt-scaled.
		// Wall-clock dt is measured so a late tick doesn't desync the rates. Cancelled on shutdown.
		//
		// Hang a system where its CADENCE belongs, not on whatever block already exists: a crossing test has to sample
		// fast to catch a boundary, a rate-based one must not. Bolting the two together silently handed discovery and
		// the area banners the regen interval as their sampling rate, which is a bug wearing a tick's clothes.
		private async Task RunTickLoop(CancellationToken cancellationToken)
		{
			using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BaseTickSeconds));
			var regen = new TickAccumulator(RegenIntervalSeconds);
			var ambientMaintain = new TickAccumulator(AmbientMaintainIntervalSeconds);
			var healthReport = new TickAccumulator(SlowTickReportSeconds);
			var lastTimestamp = Stopwatch.GetTimestamp();
			var consecutiveFailures = 0;
			try
			{
				while (await timer.WaitForNextTickAsync(cancellationToken))
				{
					var now = Stopwatch.GetTimestamp();
					var dt = Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds;
					lastTimestamp = now;

					// ONE BAD TICK MUST NOT KILL THE WORLD. This try used to sit outside the while, so any exception
					// from any tick ended the loop for the process lifetime - and silently: the listeners keep
					// accepting clients while nothing moves, regenerates, respawns, aggroes or discovers anything. A
					// transient SQLite "database is locked" from DiscoverRespawnPoint was enough to do it (a stray
					// concurrent reader is enough to produce one), and the only trace was a single log line.
					try
					{
						// Continuous-time systems: every base tick, with the actual elapsed dt. The tick returns the
						// wire-visible effects (NPC respawns, loot expiry, NPC attacks) for us to broadcast.
						foreach (var effect in World.Tick(dt))
						{
							// A dead NPC must emit NO live broadcast (move / patrol / follow / heartbeat / chatter / attack /
							// combat-mode). A move effect generated the tick it died - or one processed across an await after
							// the player's kill ran OnNpcKilled and sent the death-pose - would otherwise override that
							// death-pose and walk / re-animate the corpse, so a patrolling NPC appears to walk off and vanish
							// instead of dying in place. Only the corpse-lifecycle effects (linger->loot, respawn, fade) apply
							// once dead.
							if (effect.Entity.IsDead && effect.Type is not (WorldTickEffectType.NpcCorpseLootable or WorldTickEffectType.NpcRespawned or WorldTickEffectType.LootExpired))
								continue;

							switch (effect.Type)
							{
								case WorldTickEffectType.NpcRespawned:
								{
									await _output.BroadcastNpcRespawn(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcCorpseLootable:
								{
									await _output.ConvertCorpseToLoot(effect.Entity);
									break;
								}

								case WorldTickEffectType.LootExpired:
								case WorldTickEffectType.TimedDespawn: // a summon's lifetime ran out (#140) - same teardown
								{
									await _output.BroadcastEntityFade(effect.Entity); // graceful fade-out (n3ToClientFade)
									World.Remove(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcAttack:
								{
									// Gated by NpcAutoAttackEnabled (the once-feared respawn/world-re-entry death-loop on a
									// dense zone); the swing now plays the decoded attack-animation ack (see ApplyNpcAttack).
									if (NpcAutoAttackEnabled)
										await ApplyNpcAttack(effect.Entity, effect.Target!);
									break;
								}

								case WorldTickEffectType.NpcCast:
								{
									if (NpcAutoAttackEnabled)
										await ApplyNpcCast(effect.Entity, effect.Target!);
									break;
								}

								case WorldTickEffectType.NpcPatrol:
								{
									await _output.BroadcastNpcPatrol(effect.Entity); // idle patrol - not gated on the combat sim
									break;
								}

								case WorldTickEffectType.NpcFollow:
								{
									await _output.BroadcastNpcMove(effect.Entity); // companion-follow step - ungated (not combat), #262
									break;
								}

								case WorldTickEffectType.NpcMove:
								{
									if (NpcAutoAttackEnabled)
										await _output.BroadcastNpcMove(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcEnterCombat:
								case WorldTickEffectType.NpcExitCombat:
								{
									// Draw (enter) or sheathe (exit) the NPC's weapon for nearby viewers - #218. Gated with the
									// rest of the combat sim since combat mode only has meaning alongside the auto-attack swing.
									if (NpcAutoAttackEnabled)
									{
										await _output.BroadcastNpcCombatMode(effect.Entity, effect.Type == WorldTickEffectType.NpcEnterCombat);
										// On engage, also push the aggro so the threat/hate is visible (#216).
										if (effect is { Type: WorldTickEffectType.NpcEnterCombat, Target: not null })
											await _output.BroadcastNpcAggro(effect.Entity, effect.Target);
									}

									break;
								}

								case WorldTickEffectType.NpcShieldRose:
								{
									// #20 directional combat: the defender's blue guard arcs. Gated with the combat sim - the
									// rose only means anything when the NPC is actually fighting back.
									if (NpcAutoAttackEnabled)
										await _output.BroadcastShieldRose(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcStopped:
								{
									// The NPC arrived home and stopped; close its move with a StopIIR (#190).
									if (NpcAutoAttackEnabled)
										await _output.BroadcastNpcStop(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcHeartbeat:
								{
									// The idle-behavior pulse - ungated (fidelity, not combat), like NpcPatrol/NpcFollow; the
									// Confirmed gate in the broadcaster keeps it off a still-loading client (#225).
									if (NpcHeartbeatEnabled)
										await _output.BroadcastNpcHeartbeat(effect.Entity);
									break;
								}

								case WorldTickEffectType.EncounterBark:
								{
									// #502: the lure calls for help as a player closes. Cosmetic - it is the swap that
									// matters - but it is the tell that the encounter is armed, and it is a real captured
									// line (its `event(2)` channel is what marks these apart from ambient chatter).
									var lure = effect.Entity;
									if (lure.ChatterLines is not { Length: > 0 } barkLines)
										break;

									var heard = new HashSet<uint>();
									foreach (var near in World.ZoneForEntity(lure).PlayersInRange(lure.Position, SayRangeUnits, lure.Handle))
										heard.Add(near.Handle);
									if (heard.Count > 0)
										await _agents.NpcBark(lure.Handle, barkLines[Random.Shared.Next(barkLines.Length)], heard);
									break;
								}

								case WorldTickEffectType.EncounterTrigger:
								{
									// The trap springs: the lure is replaced by its boss on the same spot.
									await _output.SwapEncounterLureForBoss(effect.Entity);
									break;
								}

								case WorldTickEffectType.EncounterReset:
								{
									// The boss's corpse has cleared - put the lure back so it can fire again.
									await _output.RestoreEncounterLure(effect.Entity);
									break;
								}

								case WorldTickEffectType.NpcChatter:
								{
									// Ambient bystander bark: the NPC local-says a random RenownBystanderLovers line (chat
									// server CharacterChat 0x0022) to the players in say-range - purely cosmetic. Sender = the
									// NPC handle so the client shows the bubble over it.
									var npc = effect.Entity;
									if (npc.ChatterLines is not { Length: > 0 } lines)
										break;

									var recipients = new HashSet<uint>();
									foreach (var near in World.ZoneForEntity(npc).PlayersInRange(npc.Position, SayRangeUnits, npc.Handle))
										recipients.Add(near.Handle);
									if (recipients.Count > 0)
										await _agents.NpcBark(npc.Handle, lines[Random.Shared.Next(lines.Length)], recipients);
									break;
								}
							}
						}

						// Player death: push the release screen once; respawn is click-driven (RespawnMe), not timed.
						await TickPlayerDeaths();
						await TickKillChains();
						await TickBuffExpiry();
						await TickDots();

						// Boundary CROSSING tests: every base tick (10 Hz). Both used to hang off the regen accumulator
						// below, which has nothing to do with either and sampled them at 1 Hz - a walking player covers
						// several units between samples, so both fired late, well inside the boundary. In-client that read
						// as having to stand on top of a resurrection point before it registered; the radius was never the
						// problem. Cheap enough to run hot: the points/areas and the per-player discovered set are all
						// cached, so a tick is a handful of distance checks per player, and only an actual discovery
						// touches the DB.
						await DiscoverNearbyRespawnPoints();
						await TickAreaBanners();

						// Rate-based systems: fire on their own cadence, scaled by the time actually elapsed since last fire.
						if (regen.Advance(dt, out var regenElapsed))
							await RegenerateConnectedPlayers(regenElapsed);

						// #363: keep the ambient spawn-group pools at strength (respawn killed members). No-op when disabled.
						if (ambientMaintain.Advance(dt, out _))
							await _output.MaintainAmbientPools();

						consecutiveFailures = 0;
					}
					catch (OperationCanceledException)
					{
						throw; // shutdown - let the outer handler end the loop
					}
					catch (Exception ex)
					{
						// Log the first failure and then back off geometrically: a systemic fault (a broken query, a
						// permanently locked DB) would otherwise write a stack trace 10x a second, and the flood is
						// what buries the first one - the only line that says what actually broke.
						consecutiveFailures++;
						if (consecutiveFailures == 1 || consecutiveFailures % 100 == 0)
							_logger.LogError(ex, "Game tick failed ({Count} consecutive); the world keeps ticking.", consecutiveFailures);
					}

					// Measure the tick's WORK (start-of-work -> here), not the period - the period is the timer's and
					// says nothing about load. A failed tick is still a tick and is still measured: a loop that throws
					// instantly would otherwise report a perfectly healthy rate while doing nothing.
					Metrics.RecordTick(Stopwatch.GetElapsedTime(now).TotalSeconds);

					// Surface a loop that is not keeping up instead of leaving it to be noticed as "the world feels
					// laggy". Throttled to one line per SlowTickReportSeconds - the condition is by definition
					// persistent, so logging it per tick would bury it.
					if (healthReport.Advance(dt, out _))
					{
						var health = Metrics.Current;
						if (health.TicksPerSecond > 0 && health.TicksPerSecond < Metrics.NominalTicksPerSecond * SlowTickThreshold)
							_logger.LogWarning("World tick is behind: {Rate:F1} of a nominal {Nominal:F1} Hz ({Busy:P0} of the tick budget in use).",
								health.TicksPerSecond, Metrics.NominalTicksPerSecond, health.BusyFraction);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// server shutting down
			}
			catch (Exception ex)
			{
				// Only a failure of the TIMER itself reaches here now - a per-tick fault is caught above.
				_logger.LogError(ex, "Game tick loop terminated.");
			}
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

		// #348: floats the area banner when a player CROSSES into a named sub-area. A map has many - Old Tarantia
		// fires six - so this is a per-tick position test against MapArea, not a per-playfield event; entering a
		// playfield lands you in an area and that is simply the first crossing.
		//
		// The regions are CAPTURE-DERIVED (the boundary triggers are server-side - no client record references a
		// banner id), so the bounds are a hull over the positions retail was seen firing each banner at, widened by
		// AreaBannerSlack because a hull can only cover ground a captured player actually walked. That makes them
		// approximate: expect a banner slightly early/late at the edges, and expect areas we have no sighting of to
		// stay silent. It tightens as more marked dumps land.
		//
		// The SMALLEST containing area wins: the captured hulls overlap (Old Tarantia's "The West Gate" box sits
		// inside "The East Gate" box), and the tighter hull is the more specific claim.
		private async Task TickAreaBanners()
		{
			foreach (var pair in Players)
			{
				var client = pair.Key;
				var entity = pair.Value;

				var areas = await GetMapAreasCached(entity.MapId);
				if (areas.Length == 0)
					continue;

				MapArea? best = null;
				var bestVolume = double.MaxValue;
				foreach (var area in areas)
				{
					if (!Contains(area, entity.Position))
						continue;

					var volume = (double)(area.MaxX - area.MinX + 1) * (area.MaxZ - area.MinZ + 1);
					if (volume >= bestVolume)
						continue;

					bestVolume = volume;
					best = area;
				}

				var now = best?.AreaNameId ?? 0;
				if (now == entity.CurrentAreaNameId)
					continue; // still in the same area (or still in none) - a banner per tick would spam

				entity.CurrentAreaNameId = now;
				if (now == 0)
					continue; // left an area into unnamed ground - retail shows nothing

				await _output.SendAreaNameBanner(client, now);
				// Logged by id, not name: MapArea has no name (the client renders the text; decoding one needs a
				// per-page LDB pin, #291). 3373519 is "Bridge of Vilerus I" if you need to read one by hand.
				_logger.LogInformation("World: {Player} entered area {Id} on map {Map} ({Samples} captured sighting(s) back it).", client.Session.Character.Name, now, entity.MapId, best!.SampleCount);
			}
		}

		// Advances player death/respawn. A player whose vitals hit 0 starts a respawn countdown; when it elapses they
		// are revived to full and re-entered at the closest known resurrection point (RespawnPlayer). Living players are
		// untracked.
		// Drops expired buffs (those with a known duration, now imported from RDB field 0x008 into Spell.DurationMs and
		// armed at apply time, #274) and tells the client by sending the retail end-twin removal (SendBuffRemoval). The
		// client does NOT self-expire the icon - the combat captures show retail explicitly removes each timed buff
		// with the rank-1 empty-payload twin - so without this the icon counts to 0 and sticks.
		// #446: close a lapsed kill chain. Retail pairs every non-zero ChainKillIIR with a 0 about 9s later - 32 of 32
		// in the corpus - so the chain has a visible END, and without this the client would keep the last count on
		// screen forever. The kill side is CombatService.AnnounceKillChain; the window is
		// WorldEntity.ChainKillWindowSeconds, measured.
		private async Task TickKillChains()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				if (!pair.Value.TryLapseKillChain(now))
					continue;

				await _output.SendKillChain(pair.Key, pair.Value.Handle, 0);
			}
		}

		private async Task TickBuffExpiry()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				// Decide and remove in one snapshot pass BEFORE sending anything (WorldEntity.TakeExpiredBuffs). The old
				// shape - TryRemove inside a foreach over the live ConcurrentDictionary, with awaits in the body - could
				// skip a buff whose bucket neighbour had just been removed, so several buffs expiring in the same tick
				// left some behind with their icon stuck at 0s.
				var expired = pair.Value.TakeExpiredBuffs(now);
				if (expired.Count == 0)
					continue;

				// Log the BATCH before sending any of it: "several expired together and only some icons cleared" is the
				// reported failure mode, so the count here vs the per-buff lines below says immediately whether the tick
				// missed one (server side) or sent all of them and the client kept an icon (client side).
				if (expired.Count > 1)
					_logger.LogInformation("Buffs: {Count} expired together on {Player} ({Ids}).", expired.Count, pair.Key.Session.Character?.Name, string.Join(", ", expired.Select(b => $"0x{b.Key:X8}")));

				foreach (var buff in expired)
				{
					_logger.LogInformation("Buff 0x{Buff:X8} expired on {Player}.", buff.Key, pair.Key.Session.Character?.Name);
					// Mirror the apply: applyTo = the affected player, applyFrom = the caster (self for a self-buff,
					// the NPC for an NPC-cast debuff). CasterHandle carries the applyFrom (#274).
					await _output.SendBuffRemoval(pair.Key, pair.Value.Handle, buff.Value.CasterHandle, buff.Key);
				}

				// #367: the buffs' stat deltas (if any) come off with them. ONE sync after the batch - it recomputes
				// `gear + all remaining buffs` from scratch, so per-buff syncs were redundant work pushing the same
				// end state several times.
				await _output.SyncBuffStats(pair.Key);
			}
		}

		// This DoT's own tick interval, or the 2 s fallback where the captures never showed one.
		private static double IntervalOf(ActiveBuff buff)
		{
			return buff.DotIntervalMs > 0
				? buff.DotIntervalMs
				: DotTickSeconds * 1000;
		}

		private async Task TickDots()
		{
			var now = DateTimeOffset.UtcNow;
			foreach (var pair in Players)
			{
				var entity = pair.Value;
				var client = pair.Key;
				var character = client.Session.Character;

				var vitals = entity.Vitals;
				if (vitals == null || vitals.IsDead)
					continue;

				foreach (var buff in entity.ActiveBuffs.Values)
				{
					if (buff.DotDelta == 0 || (now - buff.LastDotTick).TotalMilliseconds < IntervalOf(buff))
						continue;

					buff.LastDotTick = now;

					// .godmode has to cover DAMAGE OVER TIME, not just the swing that applied it. The two direct
					// NPC-damage paths check GodMode; this one did not, so a GM in god mode was killed by a bleed -
					// and by one the NPC cast path had applied THROUGH god mode, since it creates the buff before its
					// own GodMode return. The tick is the single choke point every DoT source funnels through, which
					// is why the guard belongs here rather than at each application site.
					//
					// The stamp above still advances, so a debuff that ticked through god mode does not burst-catch-up
					// the moment it is switched off. HEALS over time are unaffected: god mode blocks harm, not healing.
					if (entity.GodMode && buff.DotDelta < 0)
						continue;

					vitals.ApplyHealthDelta(buff.DotDelta);
					await _output.SendStatDamage(client, entity.Handle, vitals.Health, buff.DotDelta, buff.DotType, buff.CasterHandle, buff.BuffId >> 8);
					await _output.PushVitals(client, vitals);

					if (vitals.IsDead)
					{
						entity.LastAttackerHandle = buff.CasterHandle; // a bleed can land the killing blow
						_logger.LogInformation("Combat: {Player} bled out from buff 0x{Buff:X8}.", character.Name, buff.BuffId);
						break;
					}
				}
			}
		}

		// A dead player gets the release screen ONCE and then waits for their RespawnMe (c2s 0x2024) click. Retail
		// respawn is purely click-driven (AssassinL23_..._resurect: the player sat dead ~757 packets before sending
		// RespawnMe), so there is NO auto-revive timer - the old 10s timer yanked the player back before the release
		// screen could be used (#214). Respawn is driven inbound by RespawnAtChosenPoint / TryRespawnNow.
		private async Task TickPlayerDeaths()
		{
			foreach (var pair in Players)
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
					character.Deaths++; // the sheet's death counter (#447)
					await _database.SaveCharacterDeadState(character.Id, true);
					await _database.SetCharacterKillCounts(character.Id, character.Kills, character.Deaths);
					await SendDeathScreen(client, character, pair.Value, pair.Value.Position);
				}
			}
		}

		private static bool Contains(MapArea area, Vector3 p)
		{
			return p.X >= area.MinX - AreaBannerSlack && p.X <= area.MaxX + AreaBannerSlack && p.Z >= area.MinZ - AreaBannerSlack && p.Z <= area.MaxZ + AreaBannerSlack &&
			       p.Y >= area.MinY - AreaBannerVerticalSlack && p.Y <= area.MaxY + AreaBannerVerticalSlack;
		}

	}
}
