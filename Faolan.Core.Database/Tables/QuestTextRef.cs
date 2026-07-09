using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One text slot of a quest definition: a <remoteformat>/<localized> reference the client resolves by
	// (RefId, Category) against its RDB.
	public class QuestTextRef : BaseEntity
	{
		[Column("QuestDefinitionId")]
		public uint QuestId { get; set; }

		[ForeignKey(nameof(QuestId))]
		public virtual QuestDefinition Quest { get; set; }

		public uint RefId { get; set; }

		// True for <localized> slots (which carry Flags and have no closing tag); false for <remoteformat>.
		public bool IsLocalized { get; set; }

		public QuestTextSlot Slot { get; set; }

		// <localized flags="...">; null/empty for remoteformat slots.
		[MaxLength(64)]
		public string Flags { get; set; }

		// The deterministic content hash from the capture (pre-escaped, e.g. contains &#36;). Render-critical:
		// the client validates it, a wrong key yields blank text.
		[MaxLength(256)]
		public string KeyText { get; set; }

		public uint Category { get; set; }
	}
}
