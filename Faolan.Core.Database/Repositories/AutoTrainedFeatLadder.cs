using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Database.Repositories
{
	/// <summary>
	///     Which of a class's auto-trained feat effects a character of a given level actually carries.
	/// </summary>
	/// <remarks>
	///     <b>The client's auto-train ladders state totals, not increments, so this is a pick and never a sum.</b>
	///     A class's skill ladder reads <c>100 @1, 200 @10, 300 @20 … 900 @75</c> for one stat across four differently
	///     named feats (<c>Climbing Skill</c> <c>Basic</c> <c>Advanced</c> <c>Expert Climbing</c>): the value at
	///     each rung is the character's whole score, not the step. Summing the rungs a level-80 has passed gives 4500
	///     where the client's answer is 900.
	///     <para>
	///         The proof is not the shape of the numbers, it is an independent path over the same data:
	///         <see cref="ClassAttributeBase" /> is imported from the very same class-progression templates (through
	///         the RDB 1000020 item parser rather than the feat catalog), it takes the value at the level rather than
	///         a sum, and it reproduces all four captured characters' pools exactly. A sum could not.
	///     </para>
	///     <para>
	///         <b>The class progression track is excluded here for exactly that reason - it is already applied.</b>
	///         <c>&lt;Class&gt; Class Feat NNN</c> is what <see cref="ClassAttributeBase" /> holds, so folding it in
	///         again would double every attribute and the health base. The rest of the auto-trained set - the skill
	///         ladders, <c>Fury of the Slave</c>, <c>Pet Mastery</c>, the class-identity feats - has no other source
	///         and is what this adds.
	///     </para>
	///     <para>
	///         <b>The rule is corroborated by three captured regen observations it was not fitted to.</b> Stat 280
	///         (Non Combat Mana Regen) is granted 2000 by the <c>Mage</c> feat (Necromancer / Herald of Xotli /
	///         Demonologist), 2000 by <c>Priest</c>, and nothing at all to the rogue and soldier classes. Added to the
	///         class base, that is 2200 for a level-80 Necromancer or Demonologist, 2000 for a level-24 Herald of Xotli
	///         (whose class templates carry no 280 row) and 0 for a level-23 Assassin - which is precisely what retail
	///         wrote in the three captures <c>RegenRates</c> hardcoded those numbers from.
	///     </para>
	/// </remarks>
	public static class AutoTrainedFeatLadder
	{
		/// <summary>One auto-trained entry the character has reached, with the effect it carries.</summary>
		/// <param name="Level">The level the client hands the feat over at - <c>FeatClassEntry.Level</c>.</param>
		/// <param name="EntryId">
		///     The client's own row id. It ascends with rank inside a ladder (<c>Pet Mastery</c>'s eight ranks are
		///     715380..715387), which is what breaks the tie when two ranks arrive at the same level - and two per
		///     level is that feat's whole schedule.
		/// </param>
		/// <param name="Effect">The stat modifier itself.</param>
		public readonly record struct Rung(uint Level, uint EntryId, FeatEffect Effect);

		/// <summary>
		///     The effects that actually apply: per stat, the rows of the single highest rung the character has
		///     reached. Rows of the same stat on that one rung are all kept - a feat that states its stat three times
		///     is three GameAction elements of one feat, which is the same thing a trained feat's own effect list is.
		/// </summary>
		public static FeatEffect[] Select(IEnumerable<Rung> reached)
		{
			return reached
				.GroupBy(r => r.Effect.StatId)
				.SelectMany(stat =>
				{
					// The winning rung of this stat's ladder. Ordered by level first and by the client's row id
					// second, so `Pet Mastery` rank 8 beats rank 7 where both arrive at level 20.
					var winner = stat.Max(r => (r.Level, r.EntryId));
					return stat.Where(r => (r.Level, r.EntryId) == winner).Select(r => r.Effect);
				})
				.ToArray();
		}
	}
}
