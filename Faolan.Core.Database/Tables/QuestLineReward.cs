using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One item reward of a quest line, from the client's quest catalog (RDB 1000104, `MainQuest.reward` /
	// `optional_reward`; see docs/rdb/quest-catalog-1000104.md). 1098 of the catalog's 2181 lines carry at least one
	// required reward and 619 carry optional ones.
	//
	// Natural composite key (QuestLineId, Pos) on a plain class - no autoincrement surrogate. The importer
	// delete+reinserts this whole set from the fixed client RDB, and a surrogate would be reassigned on every
	// re-harvest, churning thousands of faolan.sql lines for zero semantic change (see the model conventions in
	// AGENTS.md). `Pos` is the reward's slot within its line, which is the only identity it has.
	[PrimaryKey(nameof(QuestLineId), nameof(Pos))]
	public class QuestLineReward
	{
		public uint QuestLineId { get; set; }

		[ForeignKey(nameof(QuestLineId))]
		public virtual QuestLine Line { get; set; } = null!;

		public uint Pos { get; set; }

		// The rewarded item's template id. Deliberately not an FK to Items: the catalog names templates the
		// importer's item harvest has never seen (it harvests what captures reference), so an FK would reject the
		// client's own data - and EF Core 10's SQLite provider runs `foreign_key_check` after the table rebuild, so
		// it would abort the migration outright rather than fail later.
		public uint ItemTemplateId { get; set; }

		// The catalog's own `MainQuestReward.type` word - and it is **the rewarded item's `SearchCategory`**, the
		// 30000-range AH/search category, not a reward-kind enum. Measured: over the 1,635 rewards whose template we
		// have harvested it equals `Items.SearchCategory` on **1,635 of 1,635**, with no counterexample, across all
		// 27 values present. The groups read as exactly that - 30011 khopesh/broadsword, 30012 greatsword, 30017 bow,
		// 30018 crossbow, 30020 necklace, 30021 ring, 30023 cloak, 30029 ale, 30047 companion.
		//
		// Not `Items.Type`, which is the coarse 104 (or 113 for the Phoenix Medallion) on every one of them and
		// therefore separates nothing. The pairing only shows up if you compare against SearchCategory.
		//
		// What it buys: 2,308 of the 3,943 rewards name templates the item harvest has never seen (1,367 distinct),
		// and this gives every one of them a search category with no capture needed.
		//
		// The name is kept for now because renaming it is a schema change - tracked as its own follow-up rather than
		// smuggled into a decode commit. It should become `SearchCategory`.
		public uint RewardType { get; set; }

		// True for `optional_reward` - the pick-one-of-several set - and false for the guaranteed `reward` list.
		// The client has a `QuestRewardSelectionIIR_c` that is presumably the popup these drive, though that id
		// appears in no capture, so the pairing is unverified.
		public bool Optional { get; set; }

		// How many of the item the line pays - the `quantity` word of the journal record's obj3/obj4 element.
		// Null = no capture has carried this line's reward list; the catalog itself states no quantity. It is constant
		// per (line, template) on 125 of 125 captured pairs and per template on 86 of 89 - the three that vary are
		// currency tokens (Imperial Insignia, Esteem Token, Mark of Acclaim) whose amount differs per line, which is
		// why this sits here rather than on Items. A builder reading a null uses 1, the value 61% of captured elements
		// carry and the only one a non-token item has ever carried.
		public uint? Quantity { get; set; }

		// The reward's position in the journal record's obj3/obj4 list as the wire orders it. Null = never
		// captured. The catalog's `Pos` is the client's serialization order and the wire's is retail's own row order;
		// they agree on neither a reversal nor an id/type/name sort (12 captured lines, 5 counterexamples to each),
		// so the order is harvested rather than derived. A builder orders by `WirePos ?? Pos`.
		public uint? WirePos { get; set; }
	}
}
