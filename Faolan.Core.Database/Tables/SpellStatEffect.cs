using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat entry a spell carries in its RDB template's [2][9] region - a (wire stat id, amount) pair, decoded by
	// the SAME parser as item templates (RdbSpellImporter -> ItemTemplateStatModifiers, docs/rdb/item-template-stats.md,
	// #367). This region IS the client's own source for a buff's tooltip stat lines, so our stored Amount matches what
	// the client renders. 863 spells carry it (1708 rows).
	//
	// ⚠️ The region is HETEROGENEOUS and PER-STAT SCALED - NOT a clean "buff = +N stat" list, and NOT the item scale:
	//   - attributes store DISPLAY units directly (Dexterity Buff 1/2 = 0x32E +1/+2; items store the SAME attribute
	//     ×10, so ItemStatModifier's GetScale does NOT transfer to spells);
	//   - movement/percentage/rank stats store large scaled magnitudes (0x1 = 190000 on the "Mounted" mount-speed
	//     buffs; "Sustaining Wrath" ranks 108000..180000), and a health entry (0x1B) even carries a -100000000
	//     drain/kill sentinel on non-buff spells.
	// So a live apply/revert engine needs a per-stat scale table VERIFIED against in-client buff tooltips before it can
	// honor these server-side (a blind apply would corrupt combat/vitals). Kept as a decoded FOUNDATION; the apply
	// engine is deferred to that verification (#367).
	//
	// KEY is the natural (SpellId, StatId): a spell never lists a stat twice (last wire write wins on a repeat).
	// Amount is the RAW template value at whatever scale that stat id uses.
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
