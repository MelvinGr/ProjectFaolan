using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot's SUB-CLASS - the "tails" that follow the protobuf root. A plain value:
	// FullCharSnapshotSubClassCodec reads and writes it, SnapshotTails is what constructs one from a character.
	//
	// Wire order (see the codec for the grammar and its provenance):
	//   quest journal (Quest_c) | completed-quest journal | quest index | [opaque gap] | PFAccessWindow_ catalog |
	//   post-access stat block
	public sealed class FullCharSnapshotSubClass
	{
		// The quest JOURNAL region at the START of the tails (version + 0x3F1-framed Quest_c list), modeled from
		// the recovered FullPlayerCharacterIIR grammar (docs/re/fullplayer-snapshot-reader.md) instead of an opaque
		// header+string blob.
		public QuestcRegion Quests { get; init; } = new();

		// The COMPLETED-quest journal, grouped by area - the Completed tab's data, sitting directly after the active
		// journal (#390). Null for tails without one: the beach template holds no completed quests, which is exactly
		// why the region went unmodelled and every real journal's bytes fell into UnknownBefore.
		public CompletedQuestJournalRegion? CompletedQuests { get; init; }

		// The quest INDEX region (the [0x3F1] index + the "W6QT" goal-id table) between the completed journal and
		// the access-window table; modeled by QuestIndexRegion. Null when not present (then it rides UnknownBefore).
		public QuestIndexRegion? QuestIndex { get; init; }

		// Everything between the quest index and the access-window table - still opaque (decoded by
		// tools/capture/decode_snapshot_quests.py). Carried verbatim so the region round-trips byte-exact.
		public byte[] UnknownBeforeAccessWindowTable { get; init; } = [];

		// The game-static PFAccessWindow_ playfield catalog - the one tails region that is reference data rather
		// than generated. Identical for every character; AccessWindowCatalog is the typed copy production emits.
		public List<PfAccessWindowEntry> AccessWindowEntries { get; init; } = [];

		// The stat block behind the catalog. Null when the bytes there do not open a post-access block, in which
		// case they ride UnknownAfterAccessWindowTable instead.
		public SubClassPostAccessBlock? PostAccessBlock { get; init; }

		public byte[] UnknownAfterAccessWindowTable { get; init; } = [];

		public bool HasKnownRepeatedStatTable => PostAccessBlock?.HasRepeatedStatTable == true;
	}
}
