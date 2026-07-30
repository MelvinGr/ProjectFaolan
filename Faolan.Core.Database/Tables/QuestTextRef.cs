using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;
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

		// The slot's LDB DECRYPTION KEY (LdbKey), stored as the 128-bit TEA key - the same ProtoDecryptionKey shape an
		// item template uses - rather than as the 20-character wire string, which regenerates from it exactly.
		//
		// ⚠️ This was long described as "the deterministic content hash; the client validates it, a wrong key yields
		// blank text". It is not a hash and nothing is validating it: eleven LDB categories are ENCRYPTED, and this is
		// the key that decrypts them (#520). "Blank text" was the client failing to decrypt.
		public virtual ProtoDecryptionKey KeyText { get; set; } = new();

		// <localized flags="...">; null/empty for remoteformat slots.
		[MaxLength(64)]
		public string? Flags { get; set; }

		public uint Category { get; set; }
	}
}
