namespace Faolan.Core.Enums
{
	/// <summary>
	///     The <c>command</c> word of a QueuePetCommand (0x205B) packet - the pet command bar
	///     (docs/subsystems/summon-pet-decode.md).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Recovered from the client, not from the corpus. The client carries the twelve command names as one
	///         contiguous block of strings (<c>PetCommand_Assist</c> ... <c>PetCommand_AllDismiss</c>), and the block's
	///         order is the enum's order. The base is pinned by requiring the three capture-attributed codes to land on
	///         semantically matching names, and exactly one base in the plausible range does that - 0x1EF:
	///     </para>
	///     <code>
	///     0x1F4 -> AllAssist         the annotated "set all pets to assist"
	///     0x1F6 -> AllDefend         the annotated "set all pets to defend", three independent notes
	///     0x1F8 -> DismissFollower   the annotated "dismissing arcanist", confirmed by the server despawning
	///     </code>
	///     <para>
	///         No other alignment puts an assist-like, a defend-like and a dismiss-like name on those three codes; the
	///         nearest rival (base 0x1F4) matches assist and defend but puts <c>CeaseFire</c> where the capture shows a
	///         pet being despawned. A second, independent derivation agrees: the bar's ability slots Assist 264 /
	///         Follow 263 / Defend 262 / Attack 261 map to 0x1F4 / 0x1F5 / 0x1F6 / 0x1F7 in step (descending).
	///     </para>
	///     <para>
	///         The corpus carries only the five all-pets codes because the captured character was a necromancer
	///         commanding a group of pets; the per-pet codes are the client's own names at the offsets the alignment
	///         forces. Treat an unexpected code as a bug rather than a new command - the block is complete.
	///     </para>
	/// </remarks>
	public enum PetCommand : uint
	{
		/// <summary>Per-pet, addressed at the QueuePetCommand's petHandle. None of these occurs in any capture.</summary>
		Assist = 0x1EF,
		Follow = 0x1F0,
		Defend = 0x1F1,
		AttackTag = 0x1F2,
		CeaseFire = 0x1F3,

		/// <summary>All-pets. These are the five the corpus carries.</summary>
		AllAssist = 0x1F4,
		AllFollow = 0x1F5,
		AllDefend = 0x1F6,
		AllAttackTag = 0x1F7,

		/// <summary>Dismissal. <c>DismissFollower</c> is the captured one - a named follower is sent away and the server despawns it.</summary>
		DismissFollower = 0x1F8,
		Dismiss = 0x1F9,
		AllDismiss = 0x1FA
	}
}
