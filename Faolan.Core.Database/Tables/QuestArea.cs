using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Database.Tables
{
	// A quest AREA - the grouping the client's journal tabs render as the middle tier of their
	// Region > Area > quest tree. Harvested from the completed-quest journal of captured snapshots
	// (CompletedQuestJournalRegion, Gitea #390), which is the only place the area's goal-table name word appears.
	//
	// This exists because that 4CC is a property of the AREA, not of a quest. QuestDefinition.IndexMagic models it
	// per-quest, which is why only the two fully-captured quests ever carried one - and why IsSnapshotEligible
	// currently gates on a mis-attributed field (#389).
	public class QuestArea
	{
		// The area's text id - the same id its <remoteformat category="50300"> title record carries, and what
		// QuestDefinition.AreaId refers to. Stable and globally unique, so it is the PK.
		[Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint AreaId { get; set; }

		// The area's own header records, as the journal emits them: the area title (category 50300), then its map and
		// region names (both 52000). These belong to the AREA, not to any quest in it - taking them from a member
		// quest's text refs breaks as soon as that quest also appears under a different area, which is exactly what
		// left 48 of 81 captured regions unreproducible.
		public uint AreaTitleRefId { get; set; }

		public uint MapRefId { get; set; }

		public uint RegionRefId { get; set; }

		// The quests grouped under this area - the middle tier of the client's Region > Area > quest journal tree.
		public virtual ICollection<QuestDefinition> Quests { get; set; } = [];

		// The three header text records' LDB DECRYPTION KEYS, stored as 128-bit TEA keys (LdbKey) rather than as the
		// 20-character wire strings - the same ProtoDecryptionKey an item template carries. The string regenerates
		// from the words exactly (LdbKey.ToWire), and it is payload here, never a key or a lookup.
		public virtual ProtoDecryptionKey AreaTitleKey { get; set; } = new();
		public virtual ProtoDecryptionKey MapKey { get; set; } = new();
		public virtual ProtoDecryptionKey RegionKey { get; set; } = new();

		public uint AreaTitleCategory { get; set; }

		// The area's goal-table NAME word, a printable 4CC on the wire ('RRL3' 0x52524C33, 'W6QT' 0x57365154).
		public uint Magic { get; set; }
		public uint MapCategory { get; set; }
		public uint RegionCategory { get; set; }

		// The word after Magic. 0 in every captured area group; kept rather than asserted, because "constant across
		// the corpus" has repeatedly turned out to mean "the corpus only covers one case" in this protocol.
		public uint TableField1 { get; set; }
	}
}
