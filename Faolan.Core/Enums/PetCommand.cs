namespace Faolan.Core.Enums
{
	// The `command` word of a QueuePetCommand (0x205B) packet - the pet command bar (#307,
	// docs/subsystems/summon-pet-decode.md).
	//
	// RECOVERED FROM THE CLIENT, not from the corpus. The client carries the twelve command names as one contiguous
	// block of strings (`PetCommand_Assist` ... `PetCommand_AllDismiss`), and the block's ORDER is the enum's order.
	// The base is pinned by requiring the three capture-attributed codes to land on semantically matching names, and
	// **exactly one base in the plausible range does that** - 0x1EF:
	//
	//   0x1F4 -> AllAssist         the annotated "set app pets to assist"  ("app" = ALL)
	//   0x1F6 -> AllDefend         the annotated "set app pets to defend", three independent notes
	//   0x1F8 -> DismissFollower   the annotated "dismissing arcanist", confirmed by the server despawning
	//
	// No other alignment puts an assist-like, a defend-like AND a dismiss-like name on those three codes; the nearest
	// rival (base 0x1F4) matches assist and defend but puts `CeaseFire` where the capture shows a pet being despawned.
	// So the previously unattributed pair is settled: 0x1F5 is AllFollow and 0x1F7 is AllAttackTag.
	//
	// ⚠️ TWO EARLIER CONCLUSIONS DIED HERE, both worth keeping so they are not re-derived:
	//   * "The command set is CLOSED at five." It is not - there are TWELVE. The corpus carries five because the
	//     captured character was a necromancer commanding a GROUP of pets, so every press went through the all-pets
	//     bar; the per-pet codes (0x1EF-0x1F3) were simply never used in any captured session.
	//   * "There is no arithmetic relation to the bar's ability slots." There is, and it was missed because it
	//     DESCENDS: ability slots Assist 264 / Follow 263 / Defend 262 / Attack 261 map to 0x1F4 / 0x1F5 / 0x1F6 /
	//     0x1F7 in step. That is a second, independent derivation of the same pair, and it agrees.
	//
	// The five all-pets codes are the ones a capture proves; the per-pet seven are the client's own names at the
	// offsets that alignment forces. Treat an unexpected code as a bug rather than a new command - the block is
	// complete.
	public enum PetCommand : uint
	{
		// Per-pet, addressed at the QueuePetCommand's petHandle. None of these occurs in any capture (see above).
		Assist = 0x1EF,
		Follow = 0x1F0,
		Defend = 0x1F1,
		AttackTag = 0x1F2,
		CeaseFire = 0x1F3,

		// All-pets. These are the five the corpus carries.
		AllAssist = 0x1F4,
		AllFollow = 0x1F5,
		AllDefend = 0x1F6,
		AllAttackTag = 0x1F7,

		// Dismissal. `DismissFollower` is the captured one - a named follower is sent away and the server despawns it.
		DismissFollower = 0x1F8,
		Dismiss = 0x1F9,
		AllDismiss = 0x1FA
	}
}
