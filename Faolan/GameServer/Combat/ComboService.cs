using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	// A combo the character can PERFORM: the finisher's id/name + the directional input sequence (parsed from
	// Combo.Actions) pressed to trigger it, plus its GameActionId. Non-directional combos (spellweaving / reactive
	// procs) are excluded - they aren't performed by a key sequence.
	public sealed record PerformableCombo(uint Id, string Name, IReadOnlyList<ComboInput> Sequence, uint GameActionId);

	// The DATA side of combo execution (#2): the combos a character can perform = its class's SUPERSEDED combo set
	// (GetClassCombos - one entry per family at the highest qualifying rank, so a lvl-24 HoX offers Hell Strikes II,
	// not I+II) filtered to those whose Actions parse as a directional key sequence. The wire side (reading the swing
	// directions off the NormalAttack stream, matching them against these sequences, firing the finisher) builds on
	// top once the on-wire direction code is pinned from a capture.
	public sealed class ComboService
	{
		private readonly IDatabaseRepository _database;

		public ComboService(IDatabaseRepository database)
		{
			_database = database;
		}

		// The performable (directional) combos for a character of `class` at `level`, keyed by the input sequence.
		public async Task<IReadOnlyList<PerformableCombo>> GetPerformableCombosAsync(Class @class, uint level)
		{
			var combos = await _database.GetClassCombos(level, @class);
			var result = new List<PerformableCombo>();
			foreach (var combo in combos)
				if (ComboActionParser.TryParse(combo.Actions, out var sequence))
					result.Add(new PerformableCombo(combo.Id, combo.Name, sequence, combo.GameActionId));

			return result;
		}
	}
}
