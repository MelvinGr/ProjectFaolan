using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One SCRIPTED EVENT an NPC's dialogue fires (#839): retail sends a `GameActionListIIR` carrying GameAction 53241
	// `SendScryEvent`, whose payload is a bare STRING that the playfield's own script listens for. Saddur's is
	// `Fight` - it is what turns him on the player after the window closes - and Casilda fires `SpawnScavenger` (which
	// is what puts Scavenger Tuut in the world) and `RunToTortage` (her leaving for the city).
	//
	// A SET per creature rather than a column on the type, because an NPC genuinely has more than one: Casilda's two
	// are different conversations of hers. It is deliberately NOT keyed to a conversation NODE - the dev, in-client:
	// Saddur's dialogue has only one outcome, he always turns hostile, so linking the event to the screen it was
	// observed on would be machinery the data does not ask for. ⚠️ The trade is that an NPC whose dialogue fights on
	// only ONE branch would fire on all of them; nothing in the corpus does that today, and the captures still hold
	// the screen, so tightening it later is a re-harvest rather than a re-decode.
	[PrimaryKey(nameof(NpcTemplateId), nameof(Event))]
	public class NpcScriptEvent
	{
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The event string as retail sends it, verbatim - `Fight`, `SpawnScavenger`, `RunToTortage`. The server maps
		// the ones it can act on and LOGS the rest rather than ignoring them, so an unimplemented script says so.
		[MaxLength(64)]
		public string Event { get; set; } = "";
	}
}
