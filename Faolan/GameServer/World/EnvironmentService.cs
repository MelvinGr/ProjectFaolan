using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// "The world damaged you" - the non-combat damage sources (#495/#504). Before this there were none at all: every
	// health loss came from a creature through CombatResolver, which is why fall damage and lava both had to wait on
	// the same missing plumbing rather than being two small features.
	//
	// Composed collaborator (CLAUDE.md): owns the DECISION (did this move end a fall? did it cross into a hazard?) and
	// reaches the wire only through IEnvironmentOutbound. Driven from the CharDCMove handler rather than from the
	// world tick, deliberately - both triggers are edges on the player's own position stream:
	//
	//   * a fall ENDS on the move whose flag1 shows the character back on the ground, and the drop is only known then;
	//   * a hazard's own client-named trigger is `playerenter(3)`, i.e. the crossing, not "being inside".
	//
	// Polling either at the 10 Hz tick would sample the position AFTER the fact and, for a fall, miss the landing
	// record that carries the impact entirely.
	public sealed class EnvironmentService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly ConcurrentDictionary<uint, MapHazard[]> _mapHazards = new();
		private readonly IEnvironmentOutbound _output;

		public EnvironmentService(IDatabaseRepository database, IEnvironmentOutbound output, ILogger logger)
		{
			_database = database;
			_output = output;
			_logger = logger;
		}

		// Every accepted player move runs through here. `position` is the ACCEPTED position (the spellweave trance pins
		// it), so a rooted caster cannot be pushed into a hazard by a move the server rejected.
		public async Task OnPlayerMoved(INetworkClient client, WorldEntity entity, Vector3 position, MoveFlag flags)
		{
			await TrackFall(client, entity, position, flags);
			await CheckHazards(client, entity, position);
		}

		// Fall detection. The client already tells us whether it is off the ground: flag1 CLEARS NoAccelerationVector
		// while airborne, because it starts sending an acceleration vector the moment the character leaves the ground
		// (MoveFlag's own note, pinned by xxx_2clients_jumping). So this is an edge detector over that bit, not physics.
		//
		// ⚠️ The Airborne bit (0x40) is NOT the test, even though its name invites it: measured across the fall
		// captures it is set only while the character is RISING (flag1 0x42) and clears for the descent (0x02), so
		// keying on it would miss every fall that starts from a ledge rather than a jump - which is most of them.
		private async Task TrackFall(INetworkClient client, WorldEntity entity, Vector3 position, MoveFlag flags)
		{
			var airborne = !flags.HasFlag(MoveFlag.NoAccelerationVector);
			if (airborne)
			{
				if (!entity.Airborne)
				{
					entity.Airborne = true;
					// Seed the apex with the last GROUNDED height, so a fall straight off a ledge - which has no rising
					// samples at all - still measures from the top rather than from its first mid-air sample.
					entity.AirborneApexY = MathF.Max(entity.LastGroundedY, position.Y);
				}

				if (position.Y > entity.AirborneApexY)
					entity.AirborneApexY = position.Y;

				return;
			}

			entity.LastGroundedY = position.Y;
			if (!entity.Airborne)
				return;

			entity.Airborne = false;
			var fall = entity.AirborneApexY - position.Y;
			if (fall <= FallDamage.FreeFallDistance)
				return;

			var vitals = entity.Vitals;
			if (vitals == null || vitals.IsDead)
				return;

			var damage = FallDamage.Resolve(fall, vitals.MaxHealth);
			if (damage == 0)
				return;

			// .godmode spares the player the damage, but the DEBUFF still lands - it is the landing animation's marker
			// and suppressing it would make a godmode test look like the fall was not detected at all.
			var debuff = FallDamage.ResolveDebuff(damage, vitals.MaxHealth);

			if (!entity.GodMode)
			{
				vitals.ApplyHealthDelta(-(int)damage);
				// The environmental StatDamage form: no caster, no spell, school 12, and NO DamageResult - retail
				// renders no floating combat number for a fall (CombatEffectBuilder.EnvironmentalDamage).
				await _output.SendEnvironmentalDamage(client, entity.Handle, damage, (uint)CharacterStatId.HealthCurrent, FallDamage.EnvironmentalSchool);
				await _output.PushVitals(client, vitals);
			}

			if (debuff != 0)
				await _output.ApplySelfBuff(client, debuff << 8);

			// A fall CAN kill (the corpus tops out at 64.9% of max health, so two in a row will). The death itself is
			// TickPlayerDeaths' job - it notices IsDead, strips buffs and sends the release screen - so nothing here
			// duplicates that. LastAttackerHandle stays 0: there is no killer, and attributing the death to the last
			// mob that hit you would give a wrong instant-respawn (#214).
			_logger.LogInformation("Environment: {Player} fell {Fall:F1} units for {Damage} damage ({Percent:P0} of {Max}){Debuff}.",
				client.Session.Character?.Name, fall, damage, (float)damage / vitals.MaxHealth, vitals.MaxHealth,
				debuff == 0 ? "" : debuff == FallDamage.BrokenBonesSpellId ? " - Broken Bones" : " - Bruised Body");

			if (vitals.IsDead)
				_logger.LogInformation("Environment: the fall killed {Player}.", client.Session.Character?.Name);
		}

		// Hazard areas (#504). Fires on the ENTRY edge - the client's own feedback token for the lava names the trigger
		// `playerenter(3)` - so standing in one does not re-apply every move. CurrentHazardId is the "am I already
		// inside" latch, exactly like CurrentAreaNameId is for banners.
		private async Task CheckHazards(INetworkClient client, WorldEntity entity, Vector3 position)
		{
			var hazards = await GetMapHazardsCached(entity.MapId);
			if (hazards.Length == 0)
				return;

			MapHazard? inside = null;
			foreach (var hazard in hazards)
			{
				if (Contains(hazard, position))
				{
					inside = hazard;
					break;
				}
			}

			if (inside == null)
			{
				entity.CurrentHazardId = null; // left it - the next entry fires again
				return;
			}

			if (entity.CurrentHazardId == inside.Id)
				return; // still inside the one that already fired

			entity.CurrentHazardId = inside.Id;

			var vitals = entity.Vitals;
			if (vitals == null || vitals.IsDead)
				return;

			// The effect spell first, then the damage - retail's order, and the one that reads right: the lava's
			// Incineration lands at 20:25:58 and the death follows at 20:26:01, i.e. the player burns and THEN dies.
			if (inside.EffectSpellId != 0)
				await _output.ApplySelfBuff(client, inside.EffectSpellId << 8);

			if (entity.GodMode)
			{
				_logger.LogInformation("Environment: {Player} entered hazard {Hazard} on map {Map} - .godmode, so no damage.", client.Session.Character?.Name, inside.Id, entity.MapId);
				return;
			}

			if (inside.Lethal)
			{
				// Lethal is its own outcome, not "damage >= health": retail sends NO StatDamage at all for the lava
				// death (checked across the whole window), so the health simply goes to zero and TickPlayerDeaths
				// picks it up on the next tick - which is also where the death penalty is applied.
				vitals.Health = 0;
				await _output.PushVitals(client, vitals);
				_logger.LogInformation("Environment: {Player} was killed by hazard {Hazard} on map {Map}.", client.Session.Character?.Name, inside.Id, entity.MapId);
				return;
			}

			if (inside.Damage == 0)
				return;

			vitals.ApplyHealthDelta(-(int)inside.Damage);
			await _output.SendEnvironmentalDamage(client, entity.Handle, inside.Damage, (uint)CharacterStatId.HealthCurrent, FallDamage.EnvironmentalSchool);
			await _output.PushVitals(client, vitals);
			_logger.LogInformation("Environment: hazard {Hazard} on map {Map} cost {Player} {Damage} health.", inside.Id, entity.MapId, client.Session.Character?.Name, inside.Damage);
		}

		private async Task<MapHazard[]> GetMapHazardsCached(uint mapId)
		{
			if (_mapHazards.TryGetValue(mapId, out var cached))
				return cached;

			var hazards = await _database.GetMapHazards(mapId);
			_mapHazards[mapId] = hazards;
			return hazards;
		}

		// Containment against the client's own polygon - no slack anywhere, because unlike a MapArea banner hull this
		// geometry is EXACT (it is the volume the client draws the lava with). Adding tolerance to a volume that kills
		// would kill players on the walkable ledge beside it.
		//
		// Three tests, cheapest first: the Y range, then the AABB, then the polygon walk. The AABB is only a reject
		// filter - the volumes are concave ribbons tracing a lava flow (up to 583 vertices) whose bounding boxes cover
		// a lot of safe ground, so an AABB hit means "worth walking", never "inside".
		public static bool Contains(MapHazard hazard, Vector3 p)
		{
			// Ordered by Sequence: a polygon is only meaningful in ring order, and EF gives no ordering guarantee on a
			// navigation collection. Reading them as loaded produced a self-intersecting ring on some hazards.
			return InPrism(p, hazard.MinY, hazard.MaxY, hazard.MinX, hazard.MaxX, hazard.MinZ, hazard.MaxZ,
				hazard.Vertices.OrderBy(v => v.Sequence).Select(v => (v.X, v.Z)));
		}

		// The same test for an imported bounded-area region (MapRegion): resurrection-point discovery volumes (#519)
		// and quest ReachArea goal regions (#521). One routine for both tables rather than a second copy of a ray
		// cast - the ordering trap above applies identically, and it is not the kind of thing to rediscover twice.
		public static bool Contains(MapRegion region, Vector3 p)
		{
			return InPrism(p, region.MinY, region.MaxY, region.MinX, region.MaxX, region.MinZ, region.MaxZ,
				region.Vertices.OrderBy(v => v.Sequence).Select(v => (v.X, v.Z)));
		}

		// Y range -> AABB reject -> even-odd ray cast over the ring, wrapping the closing edge back to vertex 0 (the
		// RDB does not repeat the first point). `ring` must already be in Sequence order.
		private static bool InPrism(Vector3 p, float minY, float maxY, float minX, float maxX, float minZ, float maxZ,
			IEnumerable<(float X, float Z)> ring)
		{
			if (p.Y < minY || p.Y > maxY)
				return false;
			if (p.X < minX || p.X > maxX || p.Z < minZ || p.Z > maxZ)
				return false;

			var ordered = ring.ToArray();
			if (ordered.Length < 3)
				return false;

			var inside = false;
			for (int i = 0, j = ordered.Length - 1; i < ordered.Length; j = i++)
			{
				if (ordered[i].Z > p.Z != ordered[j].Z > p.Z &&
				    p.X < (ordered[j].X - ordered[i].X) * (p.Z - ordered[i].Z) / (ordered[j].Z - ordered[i].Z) + ordered[i].X)
					inside = !inside;
			}

			return inside;
		}
	}
}
