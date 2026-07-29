using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One `<parameter>` slot of an ambient bark (#493). A bark used to be assumed to carry at most one, inlined onto
	// NpcChatter as a name-only column; the Damsel in Distress line carries TWO and aborted the whole import, so the
	// slots became a child collection - the same shape ConversationScreenParameter already has for dialogue screens.
	//
	// Keyed by the parent's natural key plus the slot's ORDINAL, because the order is part of the payload: the client
	// is fed the rendered string verbatim and the slots must come back in the captured order to round-trip.
	[PrimaryKey(nameof(NpcTemplateId), nameof(Channel), nameof(SayInChat), nameof(Ordinal))]
	public class NpcChatterParameter
	{
		public uint NpcTemplateId { get; set; }

		[MaxLength(32)]
		public string Channel { get; set; } = null!;

		public uint SayInChat { get; set; }

		[ForeignKey($"{nameof(NpcTemplateId)},{nameof(Channel)},{nameof(SayInChat)}")]
		public virtual NpcChatter Chatter { get; set; } = null!;

		// Position within the bark, 0-based - the render order.
		public int Ordinal { get; set; }

		// The slot's name, e.g. `DetectedChar.name`, `Target.Name`, `helptext`.
		[MaxLength(64)]
		public string Name { get; set; } = null!;

		// The slot's value as the capture carries it (escaped exactly), or NULL for a SESSION slot whose value is
		// regenerated per send. `helptext` is content - a `<localized>` element naming a string table entry - and must
		// be stored; `DetectedChar.name` / `Target.Name` are the addressed player and must NOT be, or the capturing
		// player's real character name rides the DB into faolan.sql (#417).
		public string? Value { get; set; }
	}
}
