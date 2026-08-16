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

		// The event string as retail sends it, verbatim - `Fight`, `SpawnScavenger`, `RunToTortage`. HARVESTED, so it
		// is whatever the playfield script was told; what the server DOES about it is `Action` below.
		[MaxLength(64)]
		public string Event { get; set; } = "";

		// What this event MEANS to the server. SERVER-SIDE and editorial, exactly like `QuestGoalDefinition.GoalType`
		// beside it, and for the same reason: retail states the event STRING on the wire and nothing else, so the
		// mapping from that string to a behaviour is ours to author.
		//
		// ⭐ This is what makes a new quest's script a SEED LINE rather than a code change. The alternative - matching
		// the string in C# - means every dialogue that wants an NPC to leave needs its own `const string` and its own
		// `if`, and `RunToTortage` is a name that only ever fits Casilda. An action is the thing the strings have in
		// common, so a future `RunToKhemi` or `SpawnBandits` is wired by stamping this column.
		//
		// `None` is the default and is LOAD-BEARING, the same way `QuestGoalType.None` is: an event nobody has mapped
		// is harvested, stored, and LOGGED as unacted-on, rather than being bent into the nearest behaviour that would
		// fire wrongly. Four of the seven shipped events sit there today.
		public NpcScriptAction Action { get; set; } = NpcScriptAction.None;
	}

	// What the server does when a dialogue fires its scry event (#839). The event STRING is retail's; this is the
	// behaviour we hang on it.
	public enum NpcScriptAction
	{
		/// <summary>Harvested but not mapped - stored and logged, and nothing happens. The honest default.</summary>
		None = 0,

		/// <summary>The NPC turns on the player (Saddur's `Fight`). Runs after the window closes, where retail's does.</summary>
		TurnHostile = 1,

		/// <summary>
		///     The NPC walks to the object that opened the conversation and despawns (Casilda's `RunToTortage`).
		/// </summary>
		LeaveWorld = 2,

		/// <summary>
		///     The creatures this player's active quests are waiting on enter the world (Casilda's `SpawnScavenger`,
		///     which is what puts Scavenger Tuut on the beach).
		/// </summary>
		/// <remarks>
		///     Deliberately expressed as "reveal what the QUESTS ask for" rather than "spawn creature N": the event
		///     string names no creature, and retail's own second path agrees - on a relog Tuut arrives in the ordinary
		///     world-entry stream with no scry event at all, gated on the quest being held. So the quest is what says
		///     who should be in the world, and this event only says WHEN. Measured over 13 captures: 7 show the event
		///     followed by the spawn within the same instant, 6 show the spawn at login with no event.
		/// </remarks>
		RevealQuestTargets = 3
	}
}
