using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// The journal CONTENT of a quest: the text refs + objective marker the server ships so the client's J-panel
	// renders a real title/description/objective. Harvested from captures by the importer (QuestDefinitionExtractor)
	// - construct, don't replay. Text is never literal: each ref is a (RefId, Category) the client resolves against
	// its RDB; KeyText is the deterministic per-(id,category) content hash (see docs/quest-log-handover.md).
	// CharacterQuest persists a character's STATE (active/progress); this is the shared, character-independent
	// definition looked up by the RDB quest id.
	public class QuestDefinition : BaseEntity
	{
		// The RDB quest id (e.g. 13479) - the stable lookup key; the synthetic Id is the row PK.
		public uint QuestId { get; set; }

		// The quest's region/area id (e.g. 1054), carried raw in both the journal and full-update packets.
		public uint AreaId { get; set; }

		// The primary objective's goal id (e.g. 0x1670 = 5744), echoed in QuestGoal and the full-update.
		public uint GoalId { get; set; }

		// The objective's world-marker (map id + x/y) shown on the map; 0 when the quest has no map marker.
		public uint MapId { get; set; }
		public uint MarkerX { get; set; }
		public uint MarkerY { get; set; }

		public virtual ICollection<QuestTextRef> TextRefs { get; set; } = new List<QuestTextRef>();
	}

	// One text slot of a quest definition: a <remoteformat>/<localized> reference the client resolves by
	// (RefId, Category) against its RDB.
	public class QuestTextRef : BaseEntity
	{
		[ForeignKey(nameof(QuestDefinitionId))]
		public virtual QuestDefinition Quest { get; set; }

		public uint QuestDefinitionId { get; set; }

		public QuestTextSlot Slot { get; set; }

		public uint RefId { get; set; }
		public uint Category { get; set; }

		// The deterministic content hash from the capture (pre-escaped, e.g. contains &#36;). Render-critical:
		// the client validates it, a wrong key yields blank text.
		public string KeyText { get; set; }

		// True for <localized> slots (which carry Flags and have no closing tag); false for <remoteformat>.
		public bool IsLocalized { get; set; }

		// <localized flags="...">; null/empty for remoteformat slots.
		public string Flags { get; set; }
	}

	public enum QuestTextSlot
	{
		Title = 0,          // category 50301 - quest title line (journal + full-update)
		Summary = 1,        // category 50302 - quest summary (full-update)
		ListSubLine = 2,    // category 50303 - journal list sub-line
		Objective = 3,      // category 50304 - objective text (full-update)
		ObjectiveLocalized = 4, // <localized> objective detail (full-update)
		AreaTitle = 5,      // category 50300 - region/journal title (shared header)
		RegionName = 6,     // category 52000, id 4 - region name (shared header)
		MapName = 7         // category 52000, id = map id - map name (shared header)
	}
}
