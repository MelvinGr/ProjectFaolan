namespace Faolan.Core.Progression
{
	/// <summary>
	///     The PvP criminal system's decoded constants: the debuff spells retail applies, and the rules the client's
	///     own spell descriptions state.
	/// </summary>
	/// <remarks>
	///     Decoded from the chat-annotated gameplay capture, whose annotations walk the whole mechanic: "killed
	///     player, became criminal 36 murder points" -> "just noticed the criminal debuff i got after killing player
	///     (2m remaining)" -> "npc refued to deal with me because of ciminal" -> "now it works (debuf gone)".
	///     <para>
	///         Two independent things line up on it. On the wire, stat <c>0x449</c> - which the client's LDB string
	///         table names <b>"Murder Points"</b> - moves at the kill, and a MagicBuffIIR applies spell
	///         <c>0x3A6657</c> at the same moment and removes it 307 s later, five seconds before the "debuff gone"
	///         annotation. In the database, spell 3827287 is named <b>"Criminal"</b> with
	///         <c>DurationMs = 300000</c> - 300 s, which is that 307 s measured across a re-assert. The identification
	///         needs no inference.
	///     </para>
	///     <para>
	///         The rules are stated outright by the client. Spell 3827287's description reads "Wagoneers &amp; ship
	///         captains will not transport you and normal vendors will not trade with you. You can seek out smugglers
	///         at outlaw camps instead." - which is exactly the annotated NPC refusal, and ties the criminal state to
	///         the travel NPCs. The harsher Murderer tier (3827285, 24 h) adds "Murderers are attacked by city
	///         guards".
	///     </para>
	///     <para>
	///         What is not decoded is the ladder: how many murder points promote Criminal to Murderer, and how the
	///         pool decays. The corpus has exactly one player kill, so there is one point on that curve. The shipped
	///         spell set implies the shape - Criminal at 20 s / 300 s / 1 h and Murderer at 24 h, plus "Increase
	///         Murderpoints" (4038603) and "Reduce Murderpoints" (4038602) as the mechanism - but which threshold
	///         selects which is unmeasured, so nothing here picks one.
	///     </para>
	/// </remarks>
	public static class CriminalStatus
	{
		/// <summary>
		///     The "Criminal" debuff retail applied for a single player kill - spell 3827287, 300 s. There are four
		///     spells named "Criminal" (20 s, 300 s and two at 1 h); this is the one the capture actually shows.
		/// </summary>
		public const uint CriminalSpellId = 3827287;

		/// <summary>
		///     The "Murderer" tier - spell 3827285, 24 h. Everything Criminal does, plus city guards turning hostile.
		///     Not applied by anything yet: what promotes a criminal to a murderer is the unmeasured part.
		/// </summary>
		public const uint MurdererSpellId = 3827285;

		/// <summary>
		///     Murder points added by one player kill, in wire units, as measured: stat <c>0x449</c> went to 3500 at
		///     the kill. The player reported "36", so the client's display scale is about 100 - consistent with the
		///     rest of the stat sheet, where the displayed number and the wire number differ by a per-stat scale.
		/// </summary>
		/// <remarks>
		///     The capture then puts the stat back to 0 three seconds later, and why is not established. The natural
		///     reading is that the pool scores a threshold, converts to the criminal debuff and resets - but that is
		///     one observation of one kill, so it is written down rather than modelled.
		/// </remarks>
		public const uint PointsPerPlayerKill = 3500;

		/// <summary>How long the criminal debuff runs, matching spell 3827287's own <c>DurationMs</c>.</summary>
		public const int CriminalSeconds = 300;

		/// <summary>
		///     Whether this vendor still trades with a criminal. The client's description names the exception -
		///     "you can seek out smugglers at outlaw camps instead" - and the shipped NPCs carry it in their NAME
		///     ("Smuggler", "Tortage Smuggler", "Atzel Smuggler", "The Smuggler").
		/// </summary>
		/// <remarks>
		///     Name-based, deliberately: there is no column or captured flag that marks a vendor as an outlaw fence,
		///     and the name is the only signal the data carries. If a smuggler ever turns up whose name does not say
		///     so, this is where it breaks - which is better than a silent wrong answer.
		/// </remarks>
		public static bool TradesWithCriminals(string? vendorName) =>
			vendorName != null && vendorName.Contains("Smuggler", System.StringComparison.OrdinalIgnoreCase);

		/// <summary>
		///     Whether this travel NPC will still carry a criminal. The client's own Criminal description is the rule
		///     and it names both halves of the same sentence: "Wagoneers &amp; ship captains will not transport you and
		///     normal vendors will not trade with you. You can seek out smugglers at outlaw camps instead." The vendor
		///     half is <see cref="TradesWithCriminals" />; this is the transport half.
		/// </summary>
		/// <remarks>
		///     The smuggler exception is written to cover transport too. The description offers smugglers as the
		///     alternative without restricting that to trade, and a smuggler who sells to outlaws but refuses to move
		///     them would make the sentence's own "instead" meaningless. That reading is not separately confirmed by a
		///     capture - the corpus has no criminal attempting to travel - so it is the same name-based rule with the
		///     same caveat, and it is the conservative direction (a criminal is refused by every NPC that is not
		///     explicitly an outlaw fence).
		/// </remarks>
		public static bool TransportsCriminals(string? npcName) => TradesWithCriminals(npcName);
	}
}
