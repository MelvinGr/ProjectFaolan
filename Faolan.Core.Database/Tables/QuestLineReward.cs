using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One ITEM reward of a quest line, from the client's quest catalog (RDB 1000104, `MainQuest.reward` /
	// `optional_reward`; see docs/rdb/quest-catalog-1000104.md). 1098 of the catalog's 2181 lines carry at least one
	// required reward and 619 carry optional ones.
	//
	// Natural composite key (QuestLineId, Pos) on a plain class - no AUTOINCREMENT surrogate. The importer
	// delete+reinserts this whole set from the fixed client RDB, and a surrogate would be reassigned on every
	// re-harvest, churning thousands of faolan.sql lines for zero semantic change (see the model conventions in
	// CLAUDE.md). `Pos` is the reward's slot within its line, which is the only identity it has.
	[PrimaryKey(nameof(QuestLineId), nameof(Pos))]
	public class QuestLineReward
	{
		public uint QuestLineId { get; set; }

		[ForeignKey(nameof(QuestLineId))]
		public virtual QuestLine Line { get; set; } = null!;

		public uint Pos { get; set; }

		// The rewarded item's TEMPLATE id. ⚠️ Deliberately not an FK to Items: the catalog names templates the
		// importer's item harvest has never seen (it harvests what captures reference), so an FK would reject the
		// client's own data - and EF Core 10's SQLite provider runs `foreign_key_check` after the table rebuild, so
		// it would abort the migration outright rather than fail later.
		public uint ItemTemplateId { get; set; }

		// The catalog's own `MainQuestReward.type` word (30000, 30001, 30009, 30020, 30021, 30029, 30035, ...).
		// Carried raw: it is a small enum whose values are not decoded, and naming it before it is would be a guess.
		public uint RewardType { get; set; }

		// True for `optional_reward` - the pick-one-of-several set - and false for the guaranteed `reward` list.
		// The client has a `QuestRewardSelectionIIR_c` that is presumably the popup these drive, though that id
		// appears in no capture, so the pairing is unverified.
		public bool Optional { get; set; }
	}
}
