using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat delta a spell (buff/debuff) applies while active - a (wire stat id, amount) pair, the buff analogue of
	// ItemStatModifier. Decoded from the RDB spell template's [2][9] stat-modifier region (RdbSpellImporter ->
	// ItemTemplateStatModifiers, docs/rdb/item-template-stats.md, #367): buff spells carry their stat deltas in the
	// SAME region as item templates (e.g. Dexterity Buff = 0x32E Dex). 901 spells carry these; the rest are
	// rank-scaled/server-computed (no region).
	//
	// KEY is the natural (SpellId, StatId): a spell never carries a stat twice. Amount is the RAW template value; the
	// display/apply scale is provisional (attributes look raw, e.g. Dex Buff 1/2 = +1/+2; vitals may be ×100) - tune
	// against a buff tooltip. The engine applies these on cast and reverts on expiry.
	[PrimaryKey(nameof(SpellId), nameof(StatId))]
	public class SpellStatEffect
	{
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		public CharacterStatId StatId { get; set; }

		public int Amount { get; set; }
	}
}
