using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot's SUB-CLASS - the "tails" that follow the protobuf root. A plain value:
	// FullCharSnapshotSubClassCodec reads and writes it, SnapshotTails is what constructs one from a character.
	//
	// Wire order (see the codec for the grammar and its provenance):
	//   quest journal (Quest_c) | completed-quest journal | quest index | PFAccessWindow_ catalog |
	//   post-access stat block
	public sealed class FullCharSnapshotSubClass
	{
		// The quest JOURNAL region at the START of the tails (version + 0x3F1-framed Quest_c list), modeled from
		// the recovered FullPlayerCharacterIIR grammar (docs/re/fullplayer-snapshot-reader.md) instead of an opaque
		// header+string blob.
		public QuestcRegion Quests { get; init; } = new QuestcRegion();

		// The COMPLETED-quest journal, grouped by area - the Completed tab's data, sitting directly after the active
		// journal (#390). Null for tails without one: the beach template holds no completed quests, which is exactly
		// why the region went unmodelled and every real journal's bytes fell into UnknownBefore.
		public CompletedQuestJournalRegion? CompletedQuests { get; init; }

		// The quest INDEX region (the id list + the "W6QT" goal-id table) between the completed journal and the
		// access-window table; modeled by QuestIndexRegion. There is no gap between it and the catalog - the old
		// "UnknownBeforeAccessWindowTable" was empty on every captured subclass, and #464's re-carve made the whole
		// stream sequentially readable.
		public QuestIndexRegion? QuestIndex { get; init; }

		// The game-static PFAccessWindow_ playfield catalog - the one tails region that is reference data rather
		// than generated. Identical for every character; AccessWindowCatalog is the typed copy production emits.
		public List<PfAccessWindowEntry> AccessWindowEntries { get; init; } = [];

		// The stat block behind the catalog - the last region, read sequentially like the four before it. It used
		// to be nullable beside a verbatim `UnknownAfterAccessWindowTable` byte[] "for tails whose stat block does
		// not open with the two empty list frames"; no captured tail is one (all 184 post-catalog regions in the
		// corpus parse to their last byte, #464), and both production constructors always built the block, so the
		// fallback was dead code that kept the register carrying an "unknown" that was fully decoded.
		public SubClassPostAccessBlock PostAccessBlock { get; init; } = new SubClassPostAccessBlock();

		public bool HasKnownRepeatedStatTable => PostAccessBlock.HasRepeatedStatTable;
	}
}
