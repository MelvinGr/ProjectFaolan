using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Scripted encounters and quest ambushes: the entities a trigger puts into the world, and takes back out.
	/// </summary>
	/// <remarks>
	///     Three shapes, each of them a spawn into a zone:
	///     <list type="bullet">
	///         <item>
	///             a lure springs into its boss when a player closes on it - an entity swap rather than a re-template,
	///             which is what the capture shows: different handles in the same second;
	///         </item>
	///         <item>the boss's corpse restores the lure, so the encounter can be run again;</item>
	///         <item>a quest goal springs an ambusher in front of the player after its own delay.</item>
	///     </list>
	///     <para>
	///         Each spawns into the zone and stops there: the interest pass streams the new entity to everyone in range
	///         on its next tick, and the simulation's aggro pass puts the player on its hate list, so neither is
	///         authored here. The one thing that has to go out immediately is the lure's despawn, because the swap
	///         removes an entity clients are already rendering. It arrives as a <c>Func&lt;WorldEntity, Task&gt;</c>
	///         rather than a <see cref="WorldBroadcaster" /> reference: holding the broadcaster would close a
	///         construction cycle.
	///     </para>
	/// </remarks>
	public sealed class EncounterService
	{
		private readonly Func<WorldEntity, Task> _broadcastDespawn;
		private readonly INpcRepository _database;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// Which combos a type-built encounter NPC swings; see StampCombatKit.
		private readonly ComboService _combos;

		public EncounterService(WorldSimulation sim, INpcRepository database, RuntimeHandleAllocator handles, ComboService combos, Func<WorldEntity, Task> broadcastDespawn,
			ILogger logger)
		{
			_sim = sim;
			_database = database;
			_handles = handles;
			_combos = combos;
			_broadcastDespawn = broadcastDespawn;
			_logger = logger;
		}

		/// <summary>Replace a lure with its boss, on the lure's exact spot.</summary>
		/// <remarks>
		///     An entity swap rather than a re-template, which is what the capture shows: Damsel handle 0x0002db79 out,
		///     Eerika 0x0002ddd1 in, in the same second. The boss carries the lure's type so its corpse can restore it.
		///     <para>
		///         The boss is spawned from its type: it has no <c>NpcSpawn</c> row and must not have one, existing
		///         only inside a triggered encounter.
		///     </para>
		/// </remarks>
		public async Task<WorldEntity?> SwapEncounterLureForBoss(WorldEntity lure)
		{
			ArgumentNullException.ThrowIfNull(lure);
			if (lure.Presentation.EncounterBossTemplateId is not { } bossTemplateId)
				return null;

			var boss = await _database.GetNpcByTemplateId(bossTemplateId);
			if (boss?.StatCore?.Health is not { } bossHealth || boss.StatCore.Level is not { } bossLevel)
			{
				// Warn rather than fail silently: an encounter whose boss type was never harvested would otherwise
				// delete its lure and put nothing in its place, leaving an encounter no player could trigger again.
				_logger.LogWarning("Encounter: lure {Lure} on map {Map} has no usable boss type {Boss} - leaving the lure in place.", lure.Name, lure.MapId, bossTemplateId);
				return null;
			}

			var zone = _sim.World.ZoneForEntity(lure);
			var position = lure.Position;
			var rotation = lure.Rotation;

			// Remove the lure for every viewer that has it, then drop it from the model - the same pair
			// DespawnEntityForPlayer does, but driven from the entity rather than a caller's client.
			await _broadcastDespawn(lure);
			_sim.World.Remove(lure);

			var handle = _handles.Next();
			var body = NpcSpawnBuilder.BuildFromType(boss, handle, position, rotation, zone.PlayfieldToken,
				normalsetOverride: zone.IsEpic ? EpicDecorations.NormalsetFor(boss.StatCore) : null);
			var entity = WorldEntity.ForNpc(handle, zone.MapId, position, rotation, boss.Name, EntityVitals.FromMax(bossHealth, 0, bossHealth), boss.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = bossLevel;
			entity.Disposition = WorldEntity.DispositionHostile; // the trap has sprung - the boss attacks
			entity.Hostile = true;
			entity.LootContainer = boss.LootContainerTemplateId;
			entity.SpawnBody = body;
			await StampCombatKit(entity, boss);
			entity.Route.NoRespawn = true; // the corpse fades and the lure comes back in its place
			entity.Presentation.EncounterLureTemplateId = lure.NpcTemplateId;
			foreach (var sp in boss.Spells)
				entity.Spells.Add(new NpcSpellCast(sp.SpellId, NpcSpellEffectColumns.ToBuffData(sp.SpellId, sp.Duration, sp.DescriptorSlot3, sp.CastingLevel, sp.FlagA, sp.DescriptorSlot0, sp.DescriptorSlot8)));

			// Added to the zone only: the interest manager streams a new entity to nearby players on its next pass,
			// which is how the ambient pools spawn members mid-session too.
			zone.Add(entity);
			_logger.LogInformation("Encounter: {Lure} on map {Map} sprang into {Boss} (level {Level}) at ({X:F1}, {Z:F1}).", lure.Name, lure.MapId, boss.Name, entity.Level, position.X, position.Z);
			return entity;
		}

		/// <summary>
		///     The boss is gone: put its lure back where it stood, so the encounter can fire again.
		/// </summary>
		/// <remarks>
		///     Spawned from the lure's type at the boss's position, because the boss took the lure's exact spot, so
		///     that position is the lure's home.
		/// </remarks>
		public async Task<WorldEntity?> RestoreEncounterLure(WorldEntity boss)
		{
			ArgumentNullException.ThrowIfNull(boss);
			if (boss.Presentation.EncounterLureTemplateId is not { } lureTemplateId)
				return null;

			var lure = await _database.GetNpcByTemplateId(lureTemplateId);
			if (lure?.StatCore?.Health is not { } lureHealth || lure.StatCore.Level is not { } lureLevel)
				return null;

			var zone = _sim.World.ZoneForEntity(boss);
			var encounter = await _database.GetNpcEncounter(zone.MapId, lureTemplateId);
			var handle = _handles.Next();
			var entity = WorldEntity.ForNpc(handle, zone.MapId, boss.Position, boss.Rotation, lure.Name, EntityVitals.FromMax(lureHealth, 0, lureHealth), lure.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = lureLevel;
			entity.SpawnBody = NpcSpawnBuilder.BuildFromType(lure, handle, boss.Position, boss.Rotation, zone.PlayfieldToken,
				normalsetOverride: zone.IsEpic ? EpicDecorations.NormalsetFor(lure.StatCore) : null);
			await StampCombatKit(entity, lure);
			entity.Route.NoRespawn = true;
			entity.Presentation.ArmEncounter(encounter);

			zone.Add(entity);
			_logger.LogInformation("Encounter: {Lure} restored on map {Map} at ({X:F1}, {Z:F1}).", lure.Name, zone.MapId, boss.Position.X, boss.Position.Z);
			return entity;
		}

		/// <summary>
		///     A quest ambush: a goal advanced for the first time and springs an NPC on the player.
		/// </summary>
		/// <remarks>
		///     Spawned from its type beside the player, hostile, with no placement anywhere - the same shape as an
		///     encounter boss, triggered by quest progress instead of proximity.
		///     <para>
		///         The delay is real rather than cosmetic: in the capture the trigger effects (item grant, goal
		///         progress, feedback) all land at t=315.6 and the body arrives at t=318.6, two distinct steps. It is
		///         scheduled rather than awaited so the pickup's own reply chain is not held up behind it.
		///     </para>
		///     <para>
		///         Retail anchors the spawn on the interacted object, 3.6 units from the brick; this anchors on the
		///         player standing at it, which is the same spot to within the interaction range and needs no object
		///         identity to reach the progression engine. The offset direction is the player's own facing, so the
		///         ambusher lands in front of them.
		///     </para>
		/// </remarks>
		public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
		{
			ArgumentNullException.ThrowIfNull(client);
			ArgumentNullException.ThrowIfNull(ambush);

			var player = _sim.PlayerWorldEntity(client);
			if (player == null)
				return;

			var zone = _sim.World.ZoneForEntity(player);
			var facing = player.Rotation;
			var forward = Vector3.Transform(new Vector3(0, 0, 1), facing);
			var position = player.Position + forward * ambush.AnchorDistance;

			var delayMs = (int)MathF.Max(0, ambush.DelaySeconds * 1000);
			Task.RunAfter(delayMs, async () =>
			{
				// The player may have logged out, zoned or died in the delay - an ambush with nobody to ambush is
				// dropped rather than left standing in an empty quarry.
				if (client.Session.Account == null || _sim.PlayerWorldEntity(client) is not { } stillHere || stillHere.MapId != zone.MapId)
					return;

				await SpawnAmbusher(zone, ambush, position, facing);
			}, _logger);
		}

		/// <summary>
		///     Stamp the three combat properties a type-built NPC would otherwise be missing: what it shoots with, how
		///     far, and which combos it swings.
		/// </summary>
		/// <remarks>
		///     These NPCs are built from a template and wear nothing - <c>NpcSpawnBuilder.BuildFromType</c> is called
		///     with no outfit here, unlike a placed spawn or an ambient pool member - so the weapon-derived half
		///     answers "unarmed", which is what they are. The value is in the class half: <c>NpcStatCore.ComboClass</c>
		///     is a harvested archetype that needs no equipment to say which family a mob swings. Without this an
		///     encounter boss or a quest ambusher fights with no combo at all, whatever its type states.
		/// </remarks>
		private async Task StampCombatKit(WorldEntity entity, NpcTemplate type)
		{
			var equipment = type.Equipment;
			entity.RangedAttacker = NpcWeapon.IsRanged(equipment);
			entity.AttackRange = NpcWeapon.AttackRange(equipment, WorldState.NpcMeleeRange);
			entity.MeleeComboId = await _combos.NpcMeleeComboId(equipment);
			entity.ClassComboIds = await _combos.NpcClassComboIds(equipment, entity.Level, type.StatCore?.ComboClass);
		}

		private async Task SpawnAmbusher(Zone zone, NpcAmbush ambush, Vector3 position, Quaternion rotation)
		{
			var type = await _database.GetNpcByTemplateId(ambush.NpcTemplateId);
			if (type?.StatCore?.Health is not { } health || type.StatCore.Level is not { } level)
			{
				// Warn rather than fail silently: an ambush whose type was never harvested springs nothing at all, and
				// the quest then looks exactly like one with no ambush wired.
				_logger.LogWarning("Ambush: goal {Goal} on map {Map} names type {Type}, which has no usable stat core - nothing sprung.", ambush.GoalId, zone.MapId, ambush.NpcTemplateId);
				return;
			}

			var handle = _handles.Next();
			var entity = WorldEntity.ForNpc(handle, zone.MapId, position, rotation, type.Name, EntityVitals.FromMax(health, 0, health), type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = level;
			entity.Disposition = WorldEntity.DispositionHostile; // it is an ambush - it attacks on arrival
			entity.Hostile = true;
			entity.LootContainer = type.LootContainerTemplateId;
			entity.SpawnBody = NpcSpawnBuilder.BuildFromType(type, handle, position, rotation, zone.PlayfieldToken,
				normalsetOverride: zone.IsEpic ? EpicDecorations.NormalsetFor(type.StatCore) : null);
			await StampCombatKit(entity, type);
			entity.Route.NoRespawn = true; // the corpse fades: an ambush is a one-off, not a placement that comes back
			foreach (var sp in type.Spells)
				entity.Spells.Add(new NpcSpellCast(sp.SpellId, NpcSpellEffectColumns.ToBuffData(sp.SpellId, sp.Duration, sp.DescriptorSlot3, sp.CastingLevel, sp.FlagA, sp.DescriptorSlot0, sp.DescriptorSlot8)));

			// Zone-only, as the encounter swap and the ambient pools are: the interest pass streams it to everyone in
			// range on its next tick, and the simulation's own aggro pass puts the player on its hate list, which is
			// what the capture shows arriving with it.
			zone.Add(entity);
			_logger.LogInformation("Ambush: goal {Goal} sprang {Name} (level {Level}) on map {Map} at ({X:F1}, {Z:F1}).", ambush.GoalId, type.Name, level, zone.MapId, position.X, position.Z);
		}
	}
}
