using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One text slot of a quest definition: a <remoteformat>/<localized> reference the client resolves by
	// (RefId, Category) against its RDB.
	//
	// KEY is the natural (QuestId, Slot): a quest carries each slot at most once - both harvest paths already dedup by
	// slot within a quest (ImportOps.AddRefIfMissing / QuestDefinitionExtractor.SetRef), and it is unique across every
	// captured row. Using it as the PK (instead of a synthetic AUTOINCREMENT Id) keeps a re-harvest byte-stable: the
	// importer delete+reinserts these rows, and a surrogate id would be reassigned each time, churning faolan.sql for
	// no semantic change. QuestId doubles as the FK to QuestDefinitions.
	[PrimaryKey(nameof(QuestId), nameof(Slot))]
	public class QuestTextRef
	{
		public uint QuestId { get; set; }

		[ForeignKey(nameof(QuestId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		public uint RefId { get; set; }

		public QuestTextSlot Slot { get; set; }

		// True for <localized> slots (which carry Flags and have no closing tag); false for <remoteformat>.
		public bool IsLocalized { get; set; }

		// The deterministic content hash from the capture (pre-escaped, e.g. contains &#36;). Render-critical:
		// the client validates it, a wrong key yields blank text.
		[MaxLength(256)]
		public string KeyText { get; set; } = null!;

		// <localized flags="...">; null/empty for remoteformat slots.
		[MaxLength(64)]
		public string? Flags { get; set; }

		public uint Category { get; set; }
	}
}
