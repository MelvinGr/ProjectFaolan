using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One item of a quest's OWN reward set - what completing THIS quest pays (Gitea #893, off #815/#464).
	//
	// ⚠️ The table is named for its first third. It began as the pick-one-of-N offer harvested from
	// `QuestRewardNotifyIIR` (#815), and it is not renamed because an EF class rename scaffolds as DROP + CREATE
	// (see the `ef_renames_cross` note). What it holds now is the whole set the `Quest_c` journal record carries as
	// its `obj1`/`obj2` lists: `Optional = false` rows are GRANTED outright at completion, `Optional = true` rows
	// are OFFERED and the player picks one.
	//
	// ⭐ WHERE THE ROWS COME FROM, and why they are per QUEST while `QuestLineRewards` is per LINE. Every captured
	// journal record carries four reward lists, and the client's own renderer (FUN_01617260) plus the catalog settle
	// what they are: `obj3`/`obj4` are the LINE's guaranteed/optional reward - identical to RDB 1000104's
	// `MainQuest.reward` / `optional_reward` on 1,553 of 1,553 captured records - previewed on every step that is not
	// the line's last; `obj1`/`obj2` are this quest's own, paid at its completion (quest 10928 offered exactly its
	// obj2 and granted the pick). The own set is server-side data the catalog never states (the catalog's `Quest`
	// record has no reward field), so it is harvested here from the records and from the offers, and the
	// `--report quest-reward-lists` measurement is where both claims are re-run.
	//
	// ⭐ On a line's LAST step the line's reward FOLDS into the quest's own set (574 captured last-step records
	// carry their line's catalog lists as obj1/obj2), which is what `GetQuestRewardOptions`' catalog fallback pays
	// for un-harvested final quests - a decision when it was written, an observation now.
	//
	// Keyed by (QuestId, Pos), with the optional rows numbered densely FIRST so `Pos` on an optional row IS the
	// index the client answers with (QuestRewardSelectionIIR, 0-based into the offered list). Natural composite
	// key, no surrogate: the importer delete+reinserts the set on every re-harvest.
	[PrimaryKey(nameof(QuestId), nameof(Pos))]
	public class QuestRewardOption
	{
		// The quest whose completion offers this list.
		public uint QuestId { get; set; }

		[ForeignKey(nameof(QuestId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		// 0-based position in the offered list - the value QuestRewardSelectionIIR.Index carries.
		public uint Pos { get; set; }

		// The RDB item template granted if this option is picked (= Items.Id). Hard FK: the client renders each option
		// from its own RDB using the ProtoDecryptionKey we ship in the offer, so an un-harvested item would render as
		// "No name" - failing the import loudly is better than a nameless reward window.
		public uint ItemTemplateId { get; set; }

		[ForeignKey(nameof(ItemTemplateId))]
		public virtual Item Item { get; set; } = null!;

		// The item LEVEL the record states for this reward. It equals the item's own `Items.Level` on every captured
		// element (5,248 of 5,248 on the line half) - carried per row rather than read off the item so a quest that
		// rewards a scaled item is not silently re-levelled by an Items edit.
		public ulong Level { get; set; }

		// True = one of the choose-one options (the record's obj2, the offer's list); false = granted outright (obj1).
		// Defaults to true because every row that existed before this column was an offer option.
		public bool Optional { get; set; } = true;

		// How many of the item. 1 on the great majority; the currency-token rewards run to 25. Constant per
		// (quest, template) on every captured element, so it is quest data rather than item data.
		public uint Quantity { get; set; } = 1;
	}
}
