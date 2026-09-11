using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The FullChar snapshot's sub-class - the tails that follow the protobuf root. A plain value:
	///     <see cref="FullCharSnapshotSubClassCodec" /> reads and writes it, and <c>SnapshotTails</c> constructs one from
	///     a character.
	/// </summary>
	/// <remarks>
	///     Wire order (see the codec for the grammar and its provenance): quest journal (Quest_c), completed-quest
	///     journal, quest index, PFAccessWindow_ catalog, post-access stat block.
	/// </remarks>
	public sealed class FullCharSnapshotSubClass
	{
		// The quest journal region at the start of the tails (version plus a count-framed Quest_c list), modelled from the
		// recovered FullPlayerCharacterIIR grammar (docs/re/fullplayer-snapshot-reader.md).
		public QuestcRegion Quests { get; init; } = new QuestcRegion();

		// The completed-quest journal, grouped by area - the Completed tab's data, sitting directly after the active
		// journal. Null for tails without one: the beach template holds no completed quests.
		public CompletedQuestJournalRegion? CompletedQuests { get; init; }

		// The quest index region (the id list and the "W6QT" goal-id table) between the completed journal and the
		// access-window table; modelled by QuestIndexRegion. There is no gap between it and the catalog.
		public QuestIndexRegion? QuestIndex { get; init; }

		// The game-static PFAccessWindow_ playfield catalog - the one tails region that is reference data rather than
		// generated. Identical for every character; AccessWindowCatalog is the typed copy production emits.
		public List<PfAccessWindowEntry> AccessWindowEntries { get; init; } = [];

		// The stat block behind the catalog - the last region, read sequentially like the four before it. All 184
		// post-catalog regions in the corpus parse to their last byte.
		public SubClassPostAccessBlock PostAccessBlock { get; init; } = new SubClassPostAccessBlock();

		public bool HasKnownRepeatedStatTable => PostAccessBlock.HasRepeatedStatTable;
	}
}
