using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The tradeskill PROFESSION and RANK model (#735), recovered from the client's own feat prerequisite chains.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A profession is a strict linear chain of feats in <c>FeatPrereqs</c>: each rank names the one below it
	///         as its single prerequisite, seven deep. Eight of them ship, and the chains are contiguous in the wire
	///         id space:
	///     </para>
	///     <code>
	///     Weaponsmith  321000 -> 321001 -> 321100 -> 321101 -> 321200 -> 321201 -> 321202
	///     Armorsmith   321300 -> 321301 -> 321400 -> 321401 -> 321500 -> 321501 -> 321502
	///     Alchemy      321600 -> ...                                            -> 321802
	///     Architect    321900 -> ...                                            -> 322102
	///     Gemcutter    322200 -> ...                                            -> 322402
	///     Prospecting  323400 -> ...                                            -> 323406
	///     Weaver       327200 -> ...                                            -> 327402
	///     Skinner      327500 -> ...                                            -> 327506
	///     </code>
	///     <para>
	///         ⚠️ <b>The RANK is the chain DEPTH, never the feat's NAME.</b> All six of Gemcutter's ranks are named
	///         "Gemcutter Novice" in the shipped feat table - a naming fault in the client's own data - so a rank read
	///         off the name would give that profession seven Novices and no ladder at all. The chain is intact for it
	///         either way, which is exactly why the depth is the authority. Same class of trap as
	///         <c>feats_tree_column_is_a_name_prefix_guess</c>.
	///     </para>
	///     <para>
	///         ⚠️ <b>Only the ROOTS are stated here; the ladders themselves are DERIVED</b> from <c>FeatPrereqs</c> at
	///         load, so a re-import of the client's feat table re-derives them rather than drifting from a list. The
	///         roots have to be named because a prereq chain alone does not say what it is FOR: the WORKSHOP feats
	///         (<c>Armorsmith Workshop I -> II -> III</c>, wire 3283xx) form chains of exactly the same shape and are
	///         a guild-city BUILDING, not a profession (#761).
	///     </para>
	///     <para>
	///         ⚠️ <b>A character holds several rows of one ladder, not one.</b> <c>AddCharacterFeats</c> keeps the
	///         highest rank per <c>id / 100</c> BASE, and a ladder spans three bases (e.g. Weaponsmith 3210 / 3211 /
	///         3212), so a Grandmaster holds <c>321001</c>, <c>321101</c> and <c>321202</c> - and holds NEITHER
	///         <c>321000</c> nor <c>321201</c>. Testing "does the character hold the recipe's RequiredFeatId" is
	///         therefore wrong on its face; compare DEPTHS (<see cref="RankOf" />).
	///     </para>
	/// </remarks>
	public static class TradeskillLadders
	{
		/// <summary>The rank names, in chain order - the depth INDEX is the rank.</summary>
		public static readonly ImmutableArray<string> RankNames =
			["Novice", "Apprentice", "Adept", "Artisan", "Expert", "Master", "Grandmaster"];

		/// <summary>
		///     The root (Novice) wire feat id of each shipped profession. The NAME is ours - it is the common prefix of
		///     the chain's feat names, and Gemcutter is the reason it is not read off each rank.
		/// </summary>
		public static readonly ImmutableDictionary<string, uint> Roots = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
		{
			// The five CRAFTING professions...
			["Weaponsmith"] = 321000,
			["Armorsmith"] = 321300,
			["Alchemy"] = 321600,
			["Architect"] = 321900,
			["Gemcutter"] = 322200,
			// ...and the six GATHERING ones. Five and six is the client's own split
			// (docs/subsystems/tradeskill-recipes.md), and finding exactly eleven chains is the corroboration that
			// the roots below are the whole set rather than the ones somebody happened to look for.
			["Woodcutting"] = 323200,
			["Mining"] = 323300,
			["Prospecting"] = 323400,
			["Stonecutting"] = 323500,
			["Weaver"] = 327200,
			["Skinner"] = 327500
		}.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		///     Walks the prerequisite edges into one ladder per profession. <paramref name="requiresByFeat" /> maps a
		///     wire feat id to the single feat it requires - the shape <c>FeatPrereqs</c> stores. A chain that cannot be
		///     extended stops; nothing is invented to reach seven.
		/// </summary>
		public static ImmutableArray<TradeskillLadder> Build(IReadOnlyDictionary<uint, uint> requiresByFeat)
		{
			ArgumentNullException.ThrowIfNull(requiresByFeat);

			// Invert once: the ladder is walked ROOT-outwards, and the stored edge points the other way.
			var next = new Dictionary<uint, uint>();
			foreach (var (feat, requires) in requiresByFeat)
				next[requires] = feat;

			var ladders = new List<TradeskillLadder>();
			foreach (var (name, root) in Roots.OrderBy(r => r.Value))
			{
				var ranks = new List<uint> { root };
				var at = root;
				while (next.TryGetValue(at, out var above) && ranks.Count < RankNames.Length)
				{
					ranks.Add(above);
					at = above;
				}

				ladders.Add(new TradeskillLadder(name, [.. ranks]));
			}

			return [.. ladders];
		}

		/// <summary>
		///     The rank a set of HELD feats reaches on <paramref name="ladder" />, or null when none of it is held. The
		///     deepest held rung wins - a character legitimately holds several rungs at once (see the remarks above).
		/// </summary>
		public static int? RankOf(TradeskillLadder ladder, IReadOnlySet<uint> heldFeatIds)
		{
			ArgumentNullException.ThrowIfNull(ladder);
			ArgumentNullException.ThrowIfNull(heldFeatIds);

			for (var rank = ladder.RankFeatIds.Length - 1; rank >= 0; rank--)
			{
				if (heldFeatIds.Contains(ladder.RankFeatIds[rank]))
					return rank;
			}

			return null;
		}
	}

	/// <summary>One profession: its name, and its rank feats from Novice outward. The INDEX is the rank.</summary>
	public sealed record TradeskillLadder(string Name, ImmutableArray<uint> RankFeatIds)
	{
		/// <summary>The rank a given feat id sits at on this ladder, or null when the feat is not on it.</summary>
		public int? RankOfFeat(uint featId)
		{
			var index = RankFeatIds.IndexOf(featId);
			return index < 0
				? null
				: index;
		}

		/// <summary>The rank's display name, or the number when a ladder runs deeper than the known names.</summary>
		public static string RankName(int rank)
		{
			return rank >= 0 && rank < TradeskillLadders.RankNames.Length
				? TradeskillLadders.RankNames[rank]
				: rank.ToString();
		}
	}
}
