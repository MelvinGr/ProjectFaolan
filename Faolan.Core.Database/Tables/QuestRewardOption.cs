using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One selectable reward in a quest's pick-one-of-N offer (#815) - the list retail sends as QuestRewardNotifyIIR
	// when the quest completes, and the player answers with QuestRewardSelectionIIR.
	//
	// ⚠️ HARVESTED FROM CAPTURES, because it is nowhere else. The obvious home would be the client's own quest
	// catalog, and it is NOT there: `QuestLineRewards` is that catalog's reward list (RDB 1000104's `MainQuest.reward`
	// / `optional_reward`), and for quest 10928's line 386 it holds one row - "Exponent's Gloves" - while the wire
	// offered Acolyte's Dagger / Longsword / Staff. Those three template ids appear in NO QuestLineRewards row on any
	// line. So the option list is server-side data the client is simply told, and the only evidence we have of it is a
	// capture that watched a quest complete. Same standing as TravelDestination, which is harvested for the same
	// reason: the wire states it and no client table does.
	//
	// Keyed by (QuestId, Pos) - `Pos` IS the index the client answers with, 0-based into the offered list, so the
	// selection is a single-row lookup and nothing has to preserve list order separately. Natural composite key, no
	// surrogate: the importer delete+reinserts the set on every re-harvest.
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

		// The item LEVEL the offer states for this option (the entry's u64). 5 on all three captured options, which
		// matches the shipped Items rows - carried per option rather than read off the item so a quest that offers a
		// scaled reward is not silently re-levelled by an Items edit.
		public ulong Level { get; set; }
	}
}
