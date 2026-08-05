namespace Faolan.Core.Enums
{
	// The `command` word of a QueuePetCommand (0x205B) packet - the pet command bar (#307,
	// docs/subsystems/summon-pet-decode.md).
	//
	// The bar is FIVE buttons and the corpus carries exactly five codes, 0x1F4-0x1F8, so the set is CLOSED: whatever
	// the two unattributed ones are, they are the two remaining bar buttons and nothing else is coming.
	//
	// Three are attributed from the annotated captures. The lag direction is what makes them readable and it is settled
	// by casting-with-chat-notes, where the player NARRATES before acting, twice, ~3 s apart:
	//   05:59:13 "dismissing arcanist"   -> 05:59:16 command 0x1F8   (and the server despawns, so this one is also
	//                                                                 confirmed by BEHAVIOUR, not just the note)
	//   05:59:38 "net magus to defend"   -> 05:59:41 command 0x1F6
	// The same direction reads chat-annotated-4 consistently: "set app pets to defend" -> 0x1F6 one second later, and
	// "defend again" -> 0x1F6 one second later again. Assist is the weaker of the three - a single note two seconds
	// AFTER its press (0x1F4 at 20:11:08, "set app pets to assist" at 20:11:10) - so it is the one to re-check first if
	// a pet stance ever behaves wrongly.
	//
	// ⚠️ 0x1F5 and 0x1F7 are ATTACK and FOLLOW in an unknown order, and there is no arithmetic shortcut to guess it
	// from: the bar's ability slots (Attack 261, Defend 262, Follow 263, Assist 264, Dismiss 265) have no monotone
	// relation to the codes (Assist 264 -> 0x1F4 while Defend 262 -> 0x1F6). Both are carried as one named pair rather
	// than as two hopeful names, because a wrong name here makes a pet do the other thing - and the client sends no
	// PerformSpecialAction alongside these, so the bar press cannot be cross-referenced to its ability either.
	public enum PetCommand : uint
	{
		Assist = 0x1F4, // "assist" - the pet joins whatever the owner is fighting
		AttackOrFollowA = 0x1F5, // attack or follow - see the warning above
		Defend = 0x1F6, // "defend" - the pet guards the owner
		AttackOrFollowB = 0x1F7, // the other of attack / follow
		Dismiss = 0x1F8 // dismiss the pet (server despawns it + sends PetIIR mode-1)
	}
}
