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
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The non-combat damage sources: the world damaging the player, as against a creature doing so.
	/// </summary>
	/// <remarks>
	///     It owns the decision - did this move end a fall, did it cross into a hazard - and reaches the wire only
	///     through <see cref="IEnvironmentOutbound" />.
	///     <para>
	///         It is driven from the move handler rather than from the world tick, because both triggers are edges on
	///         the player's own position stream: a fall ends on the move whose flag1 shows the character back on the
	///         ground, and the drop is only known then; a hazard's own client-named trigger is <c>playerenter(3)</c>,
	///         the crossing rather than being inside. Polling either at the tick would sample the position after the
	///         fact and, for a fall, miss the landing record that carries the impact entirely.
	///     </para>
	/// </remarks>
	public sealed class EnvironmentService
	{
		private readonly IWorldRepository _database;
		private readonly ILogger _logger;
		private readonly ConcurrentDictionary<uint, MapHazard[]> _mapHazards = new ConcurrentDictionary<uint, MapHazard[]>();
		private readonly ConcurrentDictionary<uint, MapRegion[]> _noMountRegions = new ConcurrentDictionary<uint, MapRegion[]>();
		private readonly IEnvironmentOutbound _output;

		public EnvironmentService(IWorldRepository database, IEnvironmentOutbound output, ILogger logger)
		{
			_database = database;
			_output = output;
			_logger = logger;
		}

		/// <summary>Every accepted player move runs through here.</summary>
		/// <param name="position">
		///     The accepted position, which the spellweave trance pins, so a rooted caster cannot be pushed into a
		///     hazard by a move the server rejected.
		/// </param>
		public async Task OnPlayerMoved(INetworkClient client, WorldEntity entity, Vector3 position, MoveFlag flags, float? verticalSpeed = null)
		{
			await TrackFall(client, entity, position, flags, verticalSpeed);
			await CheckHazards(client, entity, position);
			await CheckNoMount(client, entity, position);
		}

		/// <summary>No-mount volumes: riding into a settlement unseats the rider.</summary>
		/// <remarks>
		///     The same shape as a hazard, an edge on the player's own position stream, and it reaches the wire through
		///     the same dismount the button press uses, because retail ends every dismount with one record pair
		///     whatever caused it.
		///     <para>
		///         Unlike <c>CurrentHazardId</c> there is no latch, because there is nothing to re-fire: once unseated
		///         the player is not mounted, so the test costs a bool until they mount again, and a player who mounts
		///         up inside the volume is unseated on their next move - which is the wanted behaviour, and why this is
		///         not gated on the crossing.
		///     </para>
		/// </remarks>
		private async Task CheckNoMount(INetworkClient client, WorldEntity entity, Vector3 position)
		{
			if (!entity.IsMounted)
				return;

			var regions = await GetNoMountRegionsCached(entity.MapId);
			var inside = regions.FirstOrDefault(r => Contains(r, position));
			if (inside == null)
				return;

			await _output.SendDismount(client);
			_logger.LogInformation("Environment: {Player} was unseated by no-mount volume '{Region}' on map {Map}.", client.Session.Character?.Name, inside.Name, entity.MapId);
		}

		private async Task<MapRegion[]> GetNoMountRegionsCached(uint mapId)
		{
			if (_noMountRegions.TryGetValue(mapId, out var cached))
				return cached;

			var regions = await _database.GetNoMountRegions(mapId);
			_noMountRegions[mapId] = regions;
			return regions;
		}

		/// <summary>Fall detection: an edge detector over the client's own airborne bit, not physics.</summary>
		/// <remarks>
		///     The client states whether it is off the ground: flag1 clears <c>NoAccelerationVector</c> while airborne,
		///     because it starts sending an acceleration vector the moment the character leaves the ground - see
		///     <see cref="MoveFlag" />, pinned by xxx_2clients_jumping.
		///     <para>
		///         The <c>Airborne</c> bit (0x40) is deliberately not the test, despite its name: across the fall
		///         captures it is set only while the character is rising (flag1 0x42) and clears for the descent
		///         (0x02), so keying on it would miss every fall that starts from a ledge rather than a jump, which is
		///         most of them.
		///     </para>
		/// </remarks>
		private async Task TrackFall(INetworkClient client, WorldEntity entity, Vector3 position, MoveFlag flags, float? verticalSpeed)
		{
			var airborne = !flags.HasFlag(MoveFlag.NoAccelerationVector);
			if (airborne)
			{
				if (!entity.Airborne)
				{
					entity.Airborne = true;
					// Seed the apex with the last grounded height, so a fall straight off a ledge - which has no
					// rising samples at all - still measures from the top rather than from its first mid-air sample.
					entity.AirborneApexY = MathF.Max(entity.LastGroundedY, position.Y);
					entity.AirborneFallSpeed = 0f;
				}

				if (position.Y > entity.AirborneApexY)
					entity.AirborneApexY = position.Y;

				// The client's own downward speed, which is what the damage is resolved from. Only the downward half
				// is kept, because a jump's rise reports a positive Y and says nothing about how hard the landing
				// will be, and only the maximum, because a fall accelerates.
				if (verticalSpeed is { } vy && -vy > entity.AirborneFallSpeed)
					entity.AirborneFallSpeed = -vy;

				return;
			}

			entity.LastGroundedY = position.Y;
			if (!entity.Airborne)
				return;

			entity.Airborne = false;
			var speed = entity.AirborneFallSpeed;
			entity.AirborneFallSpeed = 0f;
			var fall = entity.AirborneApexY - position.Y;

			// The speed law where the client reported a speed, the height law where it did not. Both are the same fit
			// over the same 34 corpus falls; the speed is simply the more accurate input (r = 0.96 against 0.63), and
			// it is no extra trust in the client, because the height is reconstructed from that same position stream.
			var useSpeed = speed > 0f;
			if (useSpeed ? speed <= FallDamage.FreeFallSpeed : fall <= FallDamage.FreeFallDistance)
				return;

			var vitals = entity.Vitals;
			if (vitals == null || vitals.IsDead)
				return;

			// The /falldamage toggle, deliberately after the free-fall test and before the damage is resolved: a
			// disabled fall is still a detected fall, the airborne state is cleared either way, and the toggle
			// suppresses only the consequence.
			if (!entity.FallDamageEnabled)
				return;

			var damage = useSpeed
				? FallDamage.Resolve(speed, vitals.MaxHealth)
				: FallDamage.ResolveFromDistance(fall, vitals.MaxHealth);
			if (damage == 0)
				return;

			// Godmode spares the player the damage, but the debuff still lands: it is the landing animation's marker,
			// and suppressing it would make a godmode test look like the fall was never detected.
			var debuff = FallDamage.ResolveDebuff(damage, vitals.MaxHealth);

			if (!entity.GodMode)
			{
				vitals.ApplyHealthDelta(-(int)damage);
				// The environmental damage form: no caster, no spell, school 12, and no damage result, because retail
				// renders no floating combat number for a fall.
				await _output.SendEnvironmentalDamage(client, entity.Handle, damage, (uint)CharacterStatId.HealthCurrent, FallDamage.EnvironmentalSchool);
				await _output.PushVitals(client, vitals);
			}

			if (debuff != 0)
				await _output.ApplySelfBuff(client, debuff << 8);

			// A fall can kill: the corpus tops out at 64.9% of max health, so two in a row will. The death itself is
			// the death tick's job - it notices IsDead, strips buffs and sends the release screen - so nothing here
			// duplicates it. LastAttackerHandle stays 0, because there is no killer, and attributing the death to the
			// last mob that hit the player would give a wrong instant respawn.
			_logger.LogInformation("Environment: {Player} fell {Fall:F1} units at {Speed:F1} u/s for {Damage} damage ({Percent:P0} of {Max}){Source}{Debuff}.",
				client.Session.Character?.Name, fall, speed, damage, (float)damage / vitals.MaxHealth, vitals.MaxHealth,
				useSpeed ? "" : " [no reported speed - resolved from the sampled drop]",
				debuff == 0 ? "" : debuff == FallDamage.BrokenBonesSpellId ? " - Broken Bones" : " - Bruised Body");

			if (vitals.IsDead)
				_logger.LogInformation("Environment: the fall killed {Player}.", client.Session.Character?.Name);
		}

		/// <summary>
		///     Hazard areas. Fires on the entry edge - the client's own feedback token for the lava names the trigger
		///     <c>playerenter(3)</c> - so standing in one does not re-apply on every move.
		/// </summary>
		/// <remarks>
		///     <c>CurrentHazardId</c> is the already-inside latch, as <c>CurrentAreaNameId</c> is for banners.
		/// </remarks>
		private async Task CheckHazards(INetworkClient client, WorldEntity entity, Vector3 position)
		{
			var hazards = await GetMapHazardsCached(entity.MapId);
			if (hazards.Length == 0)
				return;

			MapHazard? inside = null;
			foreach (var hazard in hazards)
			{
				if (Contains(hazard.Region, position))
				{
					inside = hazard;
					break;
				}
			}

			if (inside == null)
			{
				entity.CurrentHazardId = null; // left the hazard, so the next entry fires again
				return;
			}

			if (entity.CurrentHazardId == inside.RegionId)
				return; // still inside the one that already fired

			entity.CurrentHazardId = inside.RegionId;

			var vitals = entity.Vitals;
			if (vitals == null || vitals.IsDead)
				return;

			// The effect spell first, then the damage - retail's order: the lava's Incineration lands at 20:25:58 and
			// the death follows at 20:26:01, so the player burns and then dies.
			if (inside.EffectSpellId != 0)
				await _output.ApplySelfBuff(client, inside.EffectSpellId << 8);

			if (entity.GodMode)
			{
				_logger.LogInformation("Environment: {Player} entered hazard {Hazard} on map {Map} - .godmode, so no damage.", client.Session.Character?.Name, inside.Region.Name, entity.MapId);
				return;
			}

			if (inside.Lethal)
			{
				// Lethal is its own outcome rather than damage exceeding health: retail sends no damage record at all
				// for the lava death, checked across the whole window, so the health simply goes to zero and the
				// death tick picks it up, which is also where the death penalty is applied.
				vitals.Health = 0;
				await _output.PushVitals(client, vitals);
				_logger.LogInformation("Environment: {Player} was killed by hazard {Hazard} on map {Map}.", client.Session.Character?.Name, inside.Region.Name, entity.MapId);
				return;
			}

			if (inside.Damage == 0)
				return;

			vitals.ApplyHealthDelta(-(int)inside.Damage);
			await _output.SendEnvironmentalDamage(client, entity.Handle, inside.Damage, (uint)CharacterStatId.HealthCurrent, FallDamage.EnvironmentalSchool);
			await _output.PushVitals(client, vitals);
			_logger.LogInformation("Environment: hazard {Hazard} on map {Map} cost {Player} {Damage} health.", inside.Region.Name, entity.MapId, client.Session.Character?.Name, inside.Damage);
		}

		private async Task<MapHazard[]> GetMapHazardsCached(uint mapId)
		{
			if (_mapHazards.TryGetValue(mapId, out var cached))
				return cached;

			var hazards = await _database.GetMapHazards(mapId);
			_mapHazards[mapId] = hazards;
			return hazards;
		}

		/// <summary>Containment against the client's own polygon, with no tolerance anywhere.</summary>
		/// <remarks>
		///     Unlike a <c>MapArea</c> banner hull this geometry is exact - it is the volume the client draws the lava
		///     with, and the volume a quest goal or a resurrection pad was drawn as - and adding slack to a volume that
		///     kills would kill players on the walkable ledge beside it.
		///     <para>
		///         Three tests, cheapest first: the Y range, the bounding box, then the polygon walk. The bounding box
		///         is only a reject filter, because a lava volume is a concave ribbon tracing the flow, up to 583
		///         vertices, whose box covers a lot of safe ground: a box hit means the polygon is worth walking, never
		///         that the point is inside.
		///     </para>
		///     <para>This is the one containment routine; every consumer of region geometry goes through it.</para>
		/// </remarks>
		public static bool Contains(MapRegion region, Vector3 p)
		{
			// Ordered by Sequence: a polygon is only meaningful in ring order, and EF gives no ordering guarantee on a
			// navigation collection. Reading the vertices as loaded yields a self-intersecting ring on some volumes.
			return InPrism(p, region.MinY, region.MaxY, region.MinX, region.MaxX, region.MinZ, region.MaxZ,
				region.Vertices.OrderBy(v => v.Sequence).Select(v => (v.X, v.Z)));
		}

		/// <summary>
		///     Y range, then bounding-box reject, then an even-odd ray cast over the ring, wrapping the closing edge
		///     back to vertex 0 because the RDB does not repeat the first point.
		/// </summary>
		/// <param name="ring">The polygon's vertices, which must already be in <c>Sequence</c> order.</param>
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
