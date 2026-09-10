using System;
using System.Collections.Generic;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The ALTERNATE-ADVANCEMENT economy's rules (Gitea #150): what a kill pays into the AA xp pool, and what a
	///     full pool pays out in tokens. Pure arithmetic, so the whole thing is testable without a world.
	/// </summary>
	/// <remarks>
	///     ⭐ <b>Every number here is measured or read off the client, none is invented</b> - see
	///     <c>--report aa-xp</c> and <c>docs/re/alternate-advancement-perks.md</c>. Retail ANNOUNCES each grant in an
	///     on-screen feedback message that states the amount verbatim, and the <c>AAPvEXP</c> stat push in the same
	///     frame moves by exactly that much, so the pay-out table below is transcribed from the wire rather than
	///     fitted.
	///     <para />
	///     ⚠️ Two rows of that table are the exception and are marked at their line: creature levels 76 and 77 are
	///     UNOBSERVED, so they are linearly interpolated between the neighbours the corpus does carry. That is a
	///     placeholder, not a finding - the corpus does not license a formula, which is why this is a table.
	/// </remarks>
	public static class AlternateAdvancementRules
	{
		/// <summary>
		///     What one AA xp pool has to reach before it pays out - the client's own
		///     <c>AlternateAdvancement_XPCap</c> tweak, and the one AA tweak the client actually consumes: it divides
		///     the pool stat by this to render the "next give-away" percentage (six reads, all the same shape).
		/// </summary>
		public const uint XpCap = 1_000_000;

		// What a full pool pays. `AlternateAdvancement_{Specific,Generic}TokensAwarded` in the tuning sheet, which the
		// client never reads (0 reads against the cap's 6) - so this half of the economy is the SERVER's.
		public const int SpecificTokensAwarded = 3;
		public const int GenericTokensAwarded = 1;

		// The ceiling on UNSPENT tokens in one pool (the three `*UnusedTokensCap` tuning keys). Names absent from the
		// exe, so this is ours as well.
		public const int UnspentTokenCap = 100;

		/// <summary>
		///     How far below the player a creature may be and still pay: the GREY rule, applied to AA accrual. A
		///     creature at <c>playerLevel - 8</c> or below pays nothing.
		/// </summary>
		/// <remarks>
		///     Measured at the boundary rather than assumed: level 71 and level 72 creatures pay <b>0</b> to a level-80
		///     player across 30+ kills in eight sessions, and level 73 pays 27.
		/// </remarks>
		public const uint GreyLevelGap = 8;

		/// <summary>
		///     What a sub-cutoff BOSS pays where an ordinary grey pays nothing. Three captured bosses below the
		///     cutoff each paid exactly this - Battlemaster Torgvall at level 26 and the epic-instance Lurking Horror
		///     at 28, against a level-80 player.
		/// </summary>
		public const uint GreyEliteFloor = 2;

		/// <summary>
		///     What a creature pays by how far it sits below the player, keyed by <c>creatureLevel - playerLevel</c>
		///     (so 0 is an even-level kill and -7 is the last paying rung above the grey cutoff).
		/// </summary>
		/// <remarks>
		///     ⚠️ Repeats in the corpus are BYTE-IDENTICAL (27/27, 91/91, 211/211), so the amount is deterministic per
		///     level - there is no roll to model.
		///     <para />
		///     ⛔ -4 and -3 are INTERPOLATED and are the only two entries that are not retail's own numbers: no capture
		///     holds a level-76 or level-77 kill. They are marked so that a future capture replaces them rather than
		///     confirming them by accident.
		/// </remarks>
		private static readonly Dictionary<int, uint> PayoutByLevelGap = new()
		{
			[-7] = 27,
			[-6] = 57,
			[-5] = 91,
			[-4] = 131, // INTERPOLATED - no captured level-76 kill
			[-3] = 171, // INTERPOLATED - no captured level-77 kill
			[-2] = 211,
			[-1] = 234,
			[0] = 258
		};

		/// <summary>
		///     The AA xp a kill pays the killer, or 0 for a kill that pays nothing.
		/// </summary>
		/// <remarks>
		///     ⭐ <b><paramref name="playerLevel" /> is a GATE, not just a scale.</b> Every one of the corpus's kill
		///     grants was made by a character at the level cap, and no kill in any levelling capture pays at all -
		///     while a flat item redemption pays a Tortage lowbie 15,000. So kills feed the pool only at the cap;
		///     below it the pool moves by rewards alone.
		/// </remarks>
		/// <param name="playerLevel">the killer's level.</param>
		/// <param name="maxLevel">the level cap this server runs (the gate above).</param>
		/// <param name="creatureLevel">the killed creature's level - off the instance that died, never off a spawn row.</param>
		/// <param name="npcType">the template's <c>NpcType</c>, which is what carries the boss multiplier.</param>
		/// <param name="npcFlags">the template's flag word.</param>
		public static uint KillXp(uint playerLevel, uint maxLevel, uint creatureLevel, uint? npcType, uint? npcFlags)
		{
			if (playerLevel < maxLevel)
				return 0;

			var elite = IsElite(npcType, npcFlags);
			if (creatureLevel + GreyLevelGap <= playerLevel)
				return elite ? GreyEliteFloor : 0;

			var gap = Math.Clamp((int)creatureLevel - (int)playerLevel, -(int)GreyLevelGap + 1, 0);
			if (!PayoutByLevelGap.TryGetValue(gap, out var payout))
				return 0;

			return (uint)Math.Round(payout * Multiplier(npcType, npcFlags), MidpointRounding.AwayFromZero);
		}

		/// <summary>
		///     What a creature's template multiplies its pay-out by. Read off the two words the paying creatures
		///     differ on rather than off their reputation.
		/// </summary>
		/// <remarks>
		///     ⚠️ <b>The two factors are approximations, and the corpus says so itself.</b> Argus the Thief (level 79,
		///     <c>NpcType</c> 2) paid 461 where the ordinary level-79 kill paid 234, and Eerika of the Maidens (level
		///     80, type 2) paid 510 against 258 - ratios of 1.970 and 1.977, not 2. <b>461 is ODD</b>, which rules out
		///     an integer x2 over an integer base outright, so the real mechanism scales something other than the
		///     ordinary amount and cannot be recovered from one sample per case. 2.0 reproduces both within 1.5%.
		///     <para />
		///     Boros (level 80, type 4, <c>NpcFlags</c> 0x1C0000) paid 297 against the same 258 - a ratio of 1.151 -
		///     and the flag does NOT compound on a type-2: Eerika carries it too and still pays the type-2 rate.
		/// </remarks>
		private static double Multiplier(uint? npcType, uint? npcFlags)
		{
			if (npcType == EliteNpcType)
				return 2.0;

			return (npcFlags & EliteFlagMask) != 0 ? 1.15 : 1.0;
		}

		// The template words the multiplier reads. NpcType 2 is the named/boss tier; NpcType 3 is the one Battlemaster
		// Torgvall carries and is only ever observed below the cutoff, so it earns the floor but has no measured rate.
		private const uint EliteNpcType = 2;
		private const uint NamedNpcType = 3;
		private const uint EliteFlagMask = 0x1C0000;

		private static bool IsElite(uint? npcType, uint? npcFlags)
		{
			return npcType is EliteNpcType or NamedNpcType || (npcFlags & EliteFlagMask) != 0;
		}

		/// <summary>
		///     Drains a pool of every whole <see cref="XpCap" /> it holds, and says what those give-aways pay: one
		///     award of <see cref="SpecificTokensAwarded" /> into the pool's OWN token wallet plus
		///     <see cref="GenericTokensAwarded" /> generic ones, per full pool.
		/// </summary>
		/// <remarks>
		///     A loop rather than a single subtraction because one grant can be large enough to fill the pool more
		///     than once over - the corpus's item redemptions pay 100,000 and 125,000 at a time, and nothing stops a
		///     server from handing out more.
		/// </remarks>
		public static (int Specific, int Generic, uint Remainder) Drain(uint pool)
		{
			var giveaways = (int)(pool / XpCap);

			return (giveaways * SpecificTokensAwarded, giveaways * GenericTokensAwarded, pool % XpCap);
		}
	}
}
