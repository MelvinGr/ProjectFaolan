using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One <parameter value="7;{inner}" name="{Name}" /> of a ConversationScreen (#188). The inner element - a
	// <localized .../> or <stringentry .../> - is COLUMNIZED (see InnerElement); the once-unescaped `InnerRaw` blob
	// it used to be stored as is retired, and ConversationScreenColumns rebuilds the value byte-exactly from the
	// columns at send time. The "name" / "guildname" parameters are the live player substitution slots (regenerated
	// at send time); the rest (localized grammar tokens, npcname/npcguildname) are fixed dialogue content.
	//
	// Keyed by its position in the tree - (node, packet, screen, Sequence) - like the rest of the conversation tree,
	// so a re-import reproduces the same key (see ConversationNode). The leading three columns ARE the parent's key.
	[PrimaryKey(nameof(ConversationNodeId), nameof(PacketSequence), nameof(ScreenSequence), nameof(Sequence))]
	public class ConversationScreenParameter
	{
		public uint ConversationNodeId { get; set; }

		public int PacketSequence { get; set; } // the owning ConversationPacket's Sequence

		public int ScreenSequence { get; set; } // the owning ConversationScreen's Sequence

		[ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)},{nameof(ScreenSequence)}")]
		public virtual ConversationScreen Screen { get; set; } = null!;

		public int Sequence { get; set; } // order within the screen's parameter list

		// --- the inner element, COLUMNIZED (see InnerElement) -----------------------------------------------------
		// A parameter's value is one of two shapes here: a `<localized>` reference into the client's LDB (by numeric
		// id or by name token) or a `<stringentry>` literal. The third shape the grammar knows, a nested
		// `<remoteformat>`, appears only on world objects, so it has no column here and the importer rejects it -
		// storing a shape we cannot round-trip is what the blob used to allow.
		[ConstrainToEnumValues]
		public InnerElementKind Kind { get; set; }

		public uint? LocalizedId { get; set; }

		[MaxLength(64)]
		public string? LocalizedToken { get; set; }

		public uint? Category { get; set; }

		// The grammatical declension the localized text renders against ("", "m", "f", ...). Carried as the raw
		// attribute text, like ContentKey.
		[MaxLength(16)]
		public string Flags { get; set; } = "";

		// The per-string LDB DECRYPTION KEY (LdbKey), stored as the 128-bit TEA key - the same ProtoDecryptionKey an
		// item template carries - rather than as the 20-character wire string, which LdbKey.ToWire regenerates from it
		// exactly, retail's attribute escaping included.
		//
		// ⚠️ Long described here as a "client-validated content hash" carried as raw attribute text. It is neither: the
		// client is not validating anything, it is DECRYPTING - eleven LDB categories are encrypted and this is the key
		// (#520). Text that came out blank was the client failing to decrypt, not failing a checksum.
		public virtual ProtoDecryptionKey ContentKey { get; set; } = new();

		// A `<stringentry>` literal. EMPTY for the session slots (player name, guild, npc guild): those are
		// regenerated per send from live values and must never carry the capturing player's real name (#417).
		[MaxLength(256)]
		public string? Text { get; set; }

		[MaxLength(64)]
		public string Name { get; set; } = "";
	}
}
