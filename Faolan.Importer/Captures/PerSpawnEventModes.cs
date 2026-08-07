using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;

namespace Faolan.Importer.Captures
{
	// The four `NpcStatCore` fields that are ROLLED PER SPAWN EVENT rather than being properties of the type (#605,
	// #688): `MovementMode`, `npc_stats.SpawnAnimation`, `Stats.BccState` and `Bcc.AnimIdleId`.
	//
	// A type's core is lifted from ONE representative sighting, so whatever that NPC happened to be doing at capture
	// time would otherwise become the type's definition of itself and be inherited by every placement. The fix is to
	// tally each field over ALL of the type's sightings and keep the MODE.
	//
	// ⚠️ Not a blanket reset to a resting value. 14 types are never captured in `BccState` 1 - the Door Warden (19619)
	// is state 11 in all 15 of its sightings - so a type CAN rest somewhere other than the common value, and
	// flattening the column would trade one wrong pose for another. The mode is a measurement of that type's own
	// sightings and leaves the Door Warden where retail puts it.
	//
	// PRESENCE is preserved throughout: a field the type never sends stays null on the core, which keeps it off the
	// wire. `SpawnAnimation` in particular is a presence FLAG and never a number (#607), so "did retail send it" is
	// the only thing its column carries.
	//
	// ⚠️ This lives here, shared, because the correction used to be inline in `ImportOps.UpsertNpcTypes` and therefore
	// reached only PLACED NPCs (#689). Pets, companions and mounts are lifted by their own importers - they have no
	// `NpcSpawns` placement at all, being summoned rather than placed - so every one of them kept its representative's
	// pose. That is what shipped Courtesan 11479 in `BccState` 50 when 10 of its 11 sightings are state 1.
	public sealed class PerSpawnEventModes
	{
		private readonly Dictionary<uint, Dictionary<uint, int>> _bccStates = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _idleAnimations = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _movementModes = [];
		private readonly Dictionary<uint, Dictionary<uint, int>> _spawnAnimations = [];

		// Counts one sighting of `templateId`. Only fields retail actually SENT are tallied - protobuf-net's generated
		// getter returns 0 for an unset optional, so `ShouldSerializeX()` is the only way to tell "sent as 0" from
		// "never sent" (#607), and tallying the difference away would invent a value for every silent spawn.
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

		// Overwrites whatever the representative sighting put in the four fields with the mode over every sighting
		// observed. A field the core does not carry is left null - the type never sends it, and authoring one here
		// would put it on the wire for the first time.
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

		// Every template the tally has seen a sighting of. The pet/mount importers lift ONE representative per NAME,
		// but the tally sees them all - and a same-named sibling with no placement is otherwise never revisited by
		// anything (#689). See CorrectObservedTypes.
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

		// Applies the correction to EVERY type the tally observed a sighting of, not just the ones a caller lifted
		// (#689). The pet/mount importers keep one representative per NAME, and several creature names cover more than
		// one template - four Courtesans, two Cobras, two Chickens, two Bulls, two Drunk Pirates. The siblings the
		// representative shadows have no `NpcSpawns` placement either, so nothing in the importer ever revisits them
		// and they keep whatever their very first lift happened to catch them doing. Courtesan 11479 shipped `BccState`
		// 50 that way while its own sightings are modally 1.
		//
		// Returns the number of rows whose values actually moved, so the module can report it rather than claiming a
		// correction it did not make.
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

		// Ties broken by the LOWER value so the lift is deterministic - a dictionary's enumeration order is not a
		// tie-break, and a non-deterministic one would churn faolan.sql on every re-import for no semantic change.
		private static uint? Mode(Dictionary<uint, Dictionary<uint, int>> tallies, uint template)
		{
			return tallies.TryGetValue(template, out var tally)
				? tally.OrderByDescending(m => m.Value).ThenBy(m => m.Key).First().Key
				: null;
		}
	}
}
