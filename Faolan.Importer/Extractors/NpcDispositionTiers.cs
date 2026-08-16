using System.Collections.Generic;
using Faolan.Core.Database.Tables;

namespace Faolan.Importer.Extractors
{
	// The ORDER in which a creature's overhead disposition (cCategory) is decided, shared by the `npc-disposition`
	// module that writes it and the `--report disposition-rule` that measures it - so the number the report scores is
	// the rule the module ships (#840/#841).
	//
	// Four tiers, strongest evidence first. The first three are OBSERVATIONS out of the capture corpus; only the
	// fourth is an inference, and it exists because the corpus is finite:
	//
	//   1 OwnVotes      this template was polled - the client asked and retail answered for THIS creature.
	//   2 SeenAttacking this template was caught SWINGING at something. Its own behaviour, ranked above an average
	//                   over its faction-mates for the same reason its own poll is (#840).
	//   3 FactionRollUp the majority answer over its community's polled members (#715).
	//   4 MobRule       nothing in the corpus speaks for it OR for its community: fall back to the static columns.
	//
	// ⚠️ Tier 4 is the one that has to justify itself, because a rule that is right for one population and wrong for
	// another is exactly what caused the 2026-07-11 regression (#841): `Hostile = Community > 0` was correct for
	// wildlife and wrong for townsfolk - community 1 is the CIVILIAN/default faction shared by Citizens, vendors and
	// guards - so replacing it with the captured disposition fixed the townsfolk and left 154 types (645 placements)
	// with no answer at all, standing green and passive because a NULL reads as friendly. The beach crocodiles were
	// the reported case.
	//
	// So the rule is the community test SCOPED by the column that separates the two populations, and it was chosen by
	// measurement rather than by argument - `--report disposition-rule` scores it and its ablations against the
	// directly-polled templates, which are ground truth no tier of this chain contributed to.
	public static class NpcDispositionTiers
	{
		public const uint Friendly = 0;
		public const uint Hostile = 1;

		// The sentinel a NULL `NpcStats.community` is bucketed under. NULL is not "no faction" - it is the bucket the
		// unique story NPCs (Casilda, Kalanthes, the Acheron set) share, which is why it is a key rather than a skip.
		public const long NullCommunity = -1;

		// Community 1 is the CIVILIAN/default faction: Citizens, vendors, guards and quest-givers all sit in it. It is
		// the value that made `Community > 0` wrong, so it is excluded from the rule by name.
		public const long CivilianCommunity = 1;

		// The `cars_group` (spawn proto field 5) shared by combat creatures. Not decoded as an aggression field - it
		// is the collision/response group - but it separates the two populations cleanly: groups 0/1 hold the named
		// townsfolk (Villager, Serving Wench, Armorsmith, Tanner Chael), group 3 the mobs (Wolf, Bear, Warg, Vanir
		// Berserker, Mercenary Archer, Pict Ripper Jaw). It is a CORRELATE and is only ever consulted when the corpus
		// has said nothing - never over an observation.
		//
		// ⚠️ Retail treats the same value as LIVE state, which is separate evidence that it is the combat group and
		// not a coincidence: turning Saddur hostile restates his group to it (`NpcStatCore.MobCarsGroup`, #839).
		public const uint MobCarsGroup = NpcStatCore.MobCarsGroup;

		public enum Tier
		{
			/// <summary>Nothing in the corpus and nothing in the columns - the row stays NULL and is served friendly.</summary>
			Unknown = 0,

			/// <summary>This template's own captured <c>RequestAggroStatus</c> answers.</summary>
			OwnVotes,

			/// <summary>This template was captured attacking something.</summary>
			SeenAttacking,

			/// <summary>The majority answer over its community's polled members.</summary>
			FactionRollUp,

			/// <summary>The last-resort static rule - see <see cref="IsMob" />.</summary>
			MobRule
		}

		/// <summary>
		///     The tier-4 rule: a creature in a REAL, non-civilian faction that carries the mob collision group is
		///     hostile.
		/// </summary>
		/// <remarks>
		///     Measured, not argued - `--report disposition-rule` scores this and its ablations against the
		///     directly-polled templates. Over 430 of them: the friendly default gets 0 of 102 hostiles and 76.3%
		///     overall; `Community > 0`, the rule the regression removed, calls 159 polled-FRIENDLY templates hostile
		///     and lands BELOW the default at 39.8%; this one gets 84 of 102 at 95.3% with zero friendly-side errors.
		///     <para>
		///         The collision group ALONE scores a point higher (96 of 102, 97.4%) and is not what ships, because
		///         the two rules answer the SAME 118 templates on the population that actually reaches this tier - so
		///         the community half costs nothing today - while over ground truth it halves the neutral-side error,
		///         5 templates to 2. The slice they disagree on is the mob collision group in the civilian faction or
		///         in none, which is where the passive wildlife sits (the rabbits, hens, deer, badgers and rats retail
		///         answers NEUTRAL for). A rule that makes a rabbit bite is a visible defect; leaving a creature
		///         friendly is the status quo this tier is improving on, so the tie breaks conservative.
		///     </para>
		/// </remarks>
		public static bool IsMob(NpcStatCore core)
		{
			var community = core.Community ?? NullCommunity;
			return core.CarsGroup == MobCarsGroup && community != NullCommunity && community != CivilianCommunity;
		}

		/// <summary>Resolve one stat core through the chain, reporting WHICH tier answered.</summary>
		public static (uint? Disposition, Tier From) Resolve(
			NpcStatCore core,
			IReadOnlyDictionary<uint, uint> ownVotes,
			IReadOnlySet<uint> seenAttacking,
			IReadOnlyDictionary<long, uint> factionRollUp)
		{
			if (ownVotes.TryGetValue(core.NpcTemplateId, out var own))
				return (own, Tier.OwnVotes);

			if (seenAttacking.Contains(core.NpcTemplateId))
				return (Hostile, Tier.SeenAttacking);

			if (factionRollUp.TryGetValue(core.Community ?? NullCommunity, out var faction))
				return (faction, Tier.FactionRollUp);

			return IsMob(core)
				? (Hostile, Tier.MobRule)
				: (null, Tier.Unknown);
		}
	}
}
