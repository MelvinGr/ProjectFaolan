using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Tallies the four <see cref="NpcStatCore" /> fields that are rolled per spawn event rather than being
	///     properties of the type - MovementMode, npc_stats.SpawnAnimation, Stats.BccState and Bcc.AnimIdleId - and
	///     replaces a lifted value with the mode over every sighting.
	/// </summary>
	/// <remarks>
	///     A type's core is lifted from one representative sighting, so whatever that NPC happened to be doing at
	///     capture time would otherwise become the type's definition of itself and be inherited by every placement.
	///     The mode is a measurement of that type's own sightings, not a reset to a resting value: a type can rest
	///     somewhere other than the common value (the Door Warden, 19619, is BccState 11 in all of its sightings), so
	///     flattening the column would trade one wrong pose for another.
	///     Presence is preserved throughout: a field the type never sends stays null on the core, which keeps it off
	///     the wire. SpawnAnimation in particular is a presence flag and never a number, so whether retail sent it is
	///     the only thing its column carries.
	///     This is shared rather than inline in one importer because pets, companions and mounts are lifted by their
	///     own importers and have no NpcSpawns placement at all, being summoned rather than placed; without it they
	///     would keep their representative's pose.
	/// </remarks>
	public sealed class PerSpawnEventModes
	{
		private readonly Dictionary<uint, Dictionary<uint, int>> _bccStates = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _idleAnimations = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _movementModes = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _spawnAnimations = [];

		/// <summary>
		///     Counts one sighting of <paramref name="templateId" />.
		/// </summary>
		/// <remarks>
		///     Only fields retail actually sent are tallied: protobuf-net's generated getter returns 0 for an unset
		///     optional, so ShouldSerializeX() is the only way to tell "sent as 0" from "never sent", and tallying the
		///     difference away would invent a value for every silent spawn.
		/// </remarks>
		public void Observe(uint templateId, ProtoCoreCharacterIIR message)
		{
			if (templateId == 0 || message == null)
				return;

			if (message.ShouldSerializeMovementMode())
				Tally(_movementModes, templateId, message.MovementMode);
			if (message.npc_stats?.ShouldSerializeSpawnAnimation() == true)
				Tally(_spawnAnimations, templateId, message.npc_stats.SpawnAnimation);
			if (message.Stats?.ShouldSerializeBccState() == true)
				Tally(_bccStates, templateId, message.Stats.BccState);
			if (message.Bcc?.ShouldSerializeAnimIdleId() == true)
				Tally(_idleAnimations, templateId, message.Bcc.AnimIdleId);
		}

		/// <summary>
		///     Overwrites whatever the representative sighting put in the four fields with the mode over every sighting
		///     observed.
		/// </summary>
		/// <remarks>
		///     A field the core does not carry is left null - the type never sends it, and authoring one here would put
		///     it on the wire for the first time.
		/// </remarks>
		public void Apply(uint templateId, NpcStatCore? core)
		{
			if (core == null)
				return;

			if (core.MovementMode != null && Mode(_movementModes, templateId) is { } move)
				core.MovementMode = move;
			if (core.SpawnAnimation != null && Mode(_spawnAnimations, templateId) is { } anim)
				core.SpawnAnimation = anim;
			if (core.BccState != null && Mode(_bccStates, templateId) is { } state)
				core.BccState = state;
			if (core.AnimIdleId != null && Mode(_idleAnimations, templateId) is { } idle)
				core.AnimIdleId = idle;
		}

		/// <summary>
		///     Every template the tally has seen a sighting of.
		/// </summary>
		/// <remarks>
		///     The pet and mount importers lift one representative per name, but the tally sees them all - and a
		///     same-named sibling with no placement is otherwise never revisited. See <see cref="CorrectObservedTypes" />.
		/// </remarks>
		public IEnumerable<uint> ObservedTemplates
		{
			get
			{
				HashSet<uint> seen = [];
				foreach (var d in new[] { _movementModes, _spawnAnimations, _bccStates, _idleAnimations })
				foreach (var id in d.Keys)
					seen.Add(id);

				return seen;
			}
		}

		/// <summary>
		///     Applies the correction to every type the tally observed a sighting of, not just the ones a caller lifted.
		/// </summary>
		/// <remarks>
		///     The pet and mount importers keep one representative per name, and several creature names cover more than
		///     one template (four Courtesans, two Cobras, two Chickens, two Bulls, two Drunk Pirates). The siblings the
		///     representative shadows have no NpcSpawns placement either, so nothing else in the importer revisits them
		///     and they would keep whatever their first lift happened to catch them doing.
		/// </remarks>
		/// <returns>The number of rows whose values actually moved, so the module reports a correction it made.</returns>
		public int CorrectObservedTypes(IReadOnlyDictionary<uint, NpcStatCore> coresByTemplate)
		{
			var corrected = 0;
			foreach (var template in ObservedTemplates)
			{
				if (!coresByTemplate.TryGetValue(template, out var core))
					continue;

				var before = (core.MovementMode, core.SpawnAnimation, core.BccState, core.AnimIdleId);
				Apply(template, core);
				if (before != (core.MovementMode, core.SpawnAnimation, core.BccState, core.AnimIdleId))
					corrected++;
			}

			return corrected;
		}

		private static void Tally(Dictionary<uint, Dictionary<uint, int>> into, uint template, uint value)
		{
			var tally = into.TryGetValue(template, out var t)
				? t
				: into[template] = [];
			tally[value] = tally.GetValueOrDefault(value) + 1;
		}

		/// <remarks>
		///     Ties are broken by the lower value so the lift is deterministic - a dictionary's enumeration order is not
		///     a tie-break, and a non-deterministic one would churn faolan.sql on every re-import for no semantic change.
		/// </remarks>
		private static uint? Mode(Dictionary<uint, Dictionary<uint, int>> tallies, uint template)
		{
			return tallies.TryGetValue(template, out var tally)
				? tally.OrderByDescending(m => m.Value).ThenBy(m => m.Key).First().Key
				: null;
		}
	}
}
