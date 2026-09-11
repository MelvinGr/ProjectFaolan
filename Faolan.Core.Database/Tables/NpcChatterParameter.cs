using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One `<parameter>` slot of an ambient bark. A bark carries any number of them - the Damsel in Distress line
	// carries two - so they are a child collection rather than a name-only column on NpcChatter, the same shape
	// ConversationScreenParameter has for dialogue screens.
	//
	// Keyed by the parent's natural key plus the slot's ordinal, because the order is part of the payload: the client
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

		// The slot's value as the capture carries it (escaped exactly), or null for a session slot whose value is
		// regenerated per send. `helptext` is content - a `<localized>` element naming a string table entry - and must
		// be stored; `DetectedChar.name` / `Target.Name` are the addressed player and must not be, or the capturing
		// player's real character name rides the DB into faolan.sql.
		public string? Value { get; set; }
	}
}
