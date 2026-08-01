using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// A quest LINE - a named storyline that groups ordered quest STEPS, and the middle tier of the client's
	// Region > line > quest journal tree. This is the client's own `MainQuest` (`mainquest.cpp`, `MainQuest_c`):
	// RDB type 1000104 lists 2181 of them over 5042 quests, and `Quest.main_quest_template_id` is exactly the
	// `QuestLineId` below. Sizes run 1..13 steps, median 2.
	//
	// ⚠️ It was called `QuestLine` until 2026-08-01, which read as a PLACE. It is not one: the dev names are
	// storylines - `[Eiglophian_Mountains] Feast of Coltranach` groups the steps "Rumors of the Dragon" ->
	// "The Feast of Coltranach" -> "Through the Cave" - and the zone tag in front of the name is just a tag.
	// "Chapter" was rejected for the same reason in the other direction: AoC already uses it for the Destiny
	// Quest's chapters, which are ONE line among the 2181.
	//
	// Harvested from the completed-quest journal of captured snapshots (CompletedQuestJournalRegion, Gitea #390),
	// which is the only place the line's goal-table name word appears; the catalog fills in the rest (#569).
	public class QuestLine
	{
		// The line's text id - the same id its <remoteformat category="50300"> title record carries, what
		// QuestDefinition.QuestLineId refers to, and the client's `MainQuest.template_id`. Stable and globally
		// unique, so it is the PK.
		[Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint QuestLineId { get; set; }

		// The line's own header records, as the journal emits them: the title (category 50300), then its map and
		// region names (both 52000). These belong to the LINE, not to any quest in it - taking them from a member
		// quest's text refs breaks as soon as that quest also appears under a different line, which is exactly what
		// left 48 of 81 captured regions unreproducible.
		public uint TitleRefId { get; set; }

		public uint MapRefId { get; set; }

		public uint RegionRefId { get; set; }

		// The quest STEPS grouped under this line, in no guaranteed order - the catalog carries the line's first and
		// last step but not the order of the ones between (Quest.tier and Quest.start_new_tier are empty in the
		// shipped data), so a full ordering is still hand-authored. See ImportOps.Chain.
		public virtual ICollection<QuestDefinition> Quests { get; set; } = [];

		// The three header text records' LDB DECRYPTION KEYS, stored as 128-bit TEA keys (LdbKey) rather than as the
		// 20-character wire strings - the same ProtoDecryptionKey an item template carries. The string regenerates
		// from the words exactly (LdbKey.ToWire), and it is payload here, never a key or a lookup.
		public virtual ProtoDecryptionKey TitleKey { get; set; } = new();
		public virtual ProtoDecryptionKey MapKey { get; set; } = new();
		public virtual ProtoDecryptionKey RegionKey { get; set; } = new();

		public uint TitleCategory { get; set; }

		// The line's goal-table NAME word, a printable 4CC on the wire ('RRL3' 0x52524C33, 'W6QT' 0x57365154).
		// It is the client's `MainQuest.hash`, which is what verifies the QuestLineId <-> MainQuest.template_id
		// identity: all four tokens recovered from captures match the catalog exactly (`--report rdb-quest-catalog`).
		public uint Magic { get; set; }
		public uint MapCategory { get; set; }
		public uint RegionCategory { get; set; }

		// The word after Magic. 0 in every captured line group; kept rather than asserted, because "constant across
		// the corpus" has repeatedly turned out to mean "the corpus only covers one case" in this protocol.
		public uint TableField1 { get; set; }

		// --- from the client's quest catalog, RDB 1000104 (`MainQuest`); see docs/rdb/quest-catalog-1000104.md ---

		// The designers' name for the line, in PLAINTEXT - `[Eiglophian_Mountains] Feast of Coltranach`. Unlike the
		// three text refs above it needs no LDB page and no captured decryption key, which is why it is a column and
		// not another (Category, RefId, Key) triple. The bracketed prefix is a dev tag (zone, content pack, series), not
		// structure - do not parse it.
		[MaxLength(256)]
		public string? Name { get; set; }

		// The line's first and last STEP. The catalog gives the endpoints but NOT the order of the steps between them
		// (`Quest.tier` and `Quest.start_new_tier` are empty in the shipped data), so this does not replace the
		// hand-authored chain in ImportOps.Chain - it bounds it.
		public uint FirstQuestId { get; set; }
		public uint LastQuestId { get; set; }

		// The line that must be completed before this one opens - `MainQuest.prev_template_id`, populated on 394 of
		// the catalog's 2181 lines.
		//
		// ⚠️ Deliberately NOT a foreign key, and not a nav. It is a single-parent PREREQUISITE pointer, not the
		// reverse of an ordering: `next_template_id` is populated on just 3 rows and only 2 of the 394 links are
		// reciprocal. Two of its targets (798, 806) are not lines in the table at all, so an FK would reject the
		// catalog's own data.
		public uint? PrevQuestLineId { get; set; }

		public uint Tier { get; set; }

		// The line's ITEM rewards - `MainQuest.reward` (1098 lines) and `optional_reward` (619).
		public virtual ICollection<QuestLineReward> Rewards { get; set; } = [];
	}
}
