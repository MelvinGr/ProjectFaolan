using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Crypt;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

namespace Faolan.Importer.Rdb
{
	// Every area-banner id the CLIENT knows, and the region each one belongs to - recovered without a capture
	// (#513/#348/#679).
	//
	// A banner's wire value is not an LDB key. It is a PARAMETER inside the localized token, and the LDB key is the
	// ELF-28 hash of the whole token string:
	//
	//   <localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|3474586'>
	//   elf28("DisplayedText(3)|GenericDisplayAreaName|3474586") == 19303910 == cat 50025 "Castle Approach"
	//
	// That looks one-way, but the parameter is a small integer, so the hash INVERTS by sweeping it: compute the hash
	// for every candidate and keep the ones that hit a real category-50025 id. The prefix is constant, so its hash
	// state is computed ONCE and only the candidate's digits are hashed per step - which is what makes a sweep of tens
	// of millions cost a second rather than a minute.
	//
	// ⚠️ A hash hit is not proof on its own: ELF-28 has 2^28 buckets, so sweeping N candidates against a table of M
	// ids yields roughly N*M/2^28 collisions by chance - about half of the 1,139 hits. Which is why NOTHING is written
	// on a hit alone. A hit only becomes a row when a client REGION of the same playfield is named exactly that text
	// (MatchRegions below), and a chance collision landing on a place name that also names a notifier region on the playfield
	// its neighbours are on is not a thing that happens.
	public static class AreaBannerNames
	{
		// The category the banner strings live in, and the token the client wraps a banner id in.
		public const uint Category = 50025;
		public const string TokenPrefix = "DisplayedText(3)|GenericDisplayAreaName|";

		// The sweep ceiling. Every recovered banner block sits under 5M; this is an order of magnitude of headroom, and
		// the cost is linear and small. Lowering it would cut the chance collisions proportionally - but the block test
		// below already rejects those, and a ceiling tuned to the ids we have found is a ceiling that hides the ones we
		// have not.
		public const uint SweepMax = 40_000_000;

		// How far apart two banner ids on one playfield may sit and still count as the same BLOCK, and how far outside
		// a block a candidate may fall and still be taken.
		//
		// Real ids arrive in per-playfield runs - `358073..358089` on map 1080, `277705..277809` on Field of the Dead,
		// `4186119..4186225` on map 974 - because they were handed out a playfield at a time. A run is not gapless
		// (1080 skips 358080, 358085 and 358087) and a playfield can own SEVERAL: Old Tarantia has 3373519..3373530,
		// 3385037..3385038 and its Outlaw Camp at 3906077. So this is deliberately a cluster test rather than one
		// min..max range, and the margin is what lets a block grow past the anchors that found it.
		private const uint BlockGap = 4096;
		private const uint BlockMargin = 1024;

		// A recovered banner: the token parameter that goes on the wire, and the string the client renders for it.
		public sealed record Banner(uint Id, string Text);

		// A banner bound to the volume that fires it: the client's own region, on the client's own playfield.
		public sealed record Match(uint MapId, string RegionName, uint AreaNameId, string Text);

		// A sweep's result: the banners it recovered, and how many category-50025 entries it swept AGAINST - the second
		// is not decoration, it is the M in the collision estimate and the one number that says whether the sweep read
		// the LDB at all.
		public sealed record Recovery(IReadOnlyList<Banner> Banners, int LdbEntries)
		{
			// Roughly how many of these hits are chance collisions: N candidates against M ids over 2^28 buckets.
			public double ExpectedCollisions => (double)SweepMax * LdbEntries / (1 << 28);
		}

		// Sweeps the token parameter and returns every value whose hash is a real category-50025 id, with that id's
		// ENGLISH text. Ordered by id, which is the order the blocks read in.
		public static Recovery Recover(RdbReader rdb)
		{
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);

			// id -> English text, for the one category the banners live in.
			var byId = new Dictionary<uint, string>();
			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (languageByPage.GetValueOrDefault(e.Id, "") != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(body) != Category || LdbStringIndex.FlagsOf(body) == 1)
					continue;

				foreach (var entry in LdbStringIndex.Parse(body))
					byId[entry.Id] = entry.Value;
			}

			var prefix = Elf28.Of(TokenPrefix, 0);
			var hits = new List<Banner>();
			for (uint n = 0; n <= SweepMax; n++)
			{
				if (byId.TryGetValue(Elf28.OfDigits(n, prefix), out var text))
					hits.Add(new Banner(n, text));
			}

			return new Recovery(hits, byId.Count);
		}

		// A name reduced to what the client's two spellings of one place have in common. Nothing here is a fuzzy
		// match: every rule is a LABEL CONVENTION of the client's own, and two different strings of words never
		// collapse together under it.
		//
		//   * the widget prefix - `BA_Bridge`, `Fifo-CascadeFalls`, `BA_FIFO_ValleyOfGiants` (FIFO is the client's own
		//     name for the banner widget, so a region named after one says so);
		//   * a leading article - the region is `BA_EastGate`, the banner renders "The East Gate";
		//   * word separation - the region is written `CrawlingDunes` or `Zang Xin Camp`, the text `Crawling Dunes`;
		//   * punctuation - `Atzels Approach` against `Atzel's Approach`, `Souleaters Pass` against `Souleaters' Pass`.
		public static string Normalise(string name)
		{
			var stripped = LeadingArticle.Replace(WidgetPrefix.Replace(name.Trim(), ""), "");
			return new string(stripped.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
		}

		// Every spelling a REGION offers, because the client writes its designer bookkeeping into the same field and
		// then puts the displayed name beside it:
		//
		//   `Camp23 Atzels Bandits (Burned Farms)`   -> the banner reads "Burned Farms"
		//   `POI A - The Last Cimmerian House`       -> "The Last Cimmerian House"
		//
		// Ymir's Pass names every one of its areas the first way, which is why the join could not see that playfield
		// at all until the parenthetical was read.
		public static IEnumerable<string> RegionForms(string name)
		{
			yield return name;

			var parenthetical = Parenthetical.Match(name);
			if (parenthetical.Success)
				yield return parenthetical.Groups[1].Value;

			var poi = PoiPrefix.Replace(name, "");
			if (poi != name)
				yield return poi;
		}

		// Every spelling a banner TEXT offers. Only one rule, and it is the client contradicting itself: an internal
		// name keeps the possessive s on some places and drops it on others - `Ymirs Tongue` for "Ymir's Tongue" but
		// `BA_LionMarket` for "The Lion's Market". Since Normalise drops the apostrophe either way, the two spellings
		// need to be offered as ALTERNATIVES rather than one being normalised into the other - collapsing every
		// possessive would fix `LionMarket` and break `YmirsTongue` in the same stroke.
		public static IEnumerable<string> TextForms(string text)
		{
			yield return text;

			var depossessed = Possessive.Replace(text, "");
			if (depossessed != text)
				yield return depossessed;
		}

		private static readonly Regex WidgetPrefix = new Regex(@"^(BA[_-])?(FIFO[_-])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex LeadingArticle = new Regex(@"^the\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex Parenthetical = new Regex(@"\(([^)]+)\)", RegexOptions.Compiled);
		private static readonly Regex PoiPrefix = new Regex(@"^POI\s+\S+\s*-\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static readonly Regex Possessive = new Regex(@"(?<=\w)'s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Binds recovered banners to the client's crossing-notifier regions, per playfield.
		//
		// Two gates, and the second is what makes the first safe:
		//
		//   1. NAME. The region's normalised name is the banner's normalised text. On its own this is ambiguous far
		//      more often than not - 41 different ids render "Outlaw Camp", one per playfield that has one - and it is
		//      also the gate a chance collision can pass, since a collision's text is drawn from the same table of
		//      place names.
		//   2. BLOCK. The id must sit in one of the PLAYFIELD's id blocks. A block is a cluster of ANCHORS: ids that
		//      are already pinned to this map, either because exactly one recovered id in the whole client bears that
		//      text (so there is nothing to choose) or because a capture recorded retail sending it here. A cluster of
		//      one anchor counts only when that anchor is captured - wire truth needs no corroboration, a lone name
		//      match does.
		//
		// Gate 2 is what turns "Outlaw Camp" from 41 candidates into one, and it is also what rejects a name match
		// that lands nowhere near the playfield's own numbering - map 1010's `Fifo-CimmerianSettlement` matches the
		// text "Cimmerian Settlement" at 711411 uniquely, while every other banner on that playfield is 3709826..3709830,
		// so the id belongs to some other place of the same name and the region is left unresolved.
		//
		// ⚠️ **Where the WIRE has pinned a playfield's numbering, only a block holding a captured id counts** (#680).
		// The same places are named on several playfields - Ymir's Pass's areas appear under 269xxx, 3822xxx AND
		// 3908xxx - so a playfield's regions can name-match a whole coherent-looking cluster that belongs to a copy of
		// it. On Ymir's Pass that cluster is 3908476..3908749 and it matched EIGHT regions, while the two banners
		// retail was actually captured sending there are 269294 and 269449. Without this the widened forms below would
		// have imported all eight, and every one of them would have been another playfield's id.
		//
		// A region with SEVERAL surviving candidates is left unresolved and reported, never guessed at.
		public static List<Match> MatchRegions(
			IReadOnlyDictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			IReadOnlyList<Banner> banners,
			IReadOnlyDictionary<uint, IReadOnlyCollection<uint>> capturedByMap,
			List<string> notes)
		{
			var byText = new Dictionary<string, List<uint>>();
			foreach (var banner in banners)
			foreach (var key in TextForms(banner.Text).Select(Normalise).Distinct())
			{
				if (!byText.TryGetValue(key, out var ids))
					byText[key] = ids = [];
				if (!ids.Contains(banner.Id))
					ids.Add(banner.Id);
			}

			var textById = banners.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Text);

			var matched = new List<Match>();
			foreach (var (mapId, areas) in areasByMap.OrderBy(kv => kv.Key))
			{
				var notifiers = areas
					.Where(a => a is { IsPolygon: true, ScryId: BoundedAreaClassifier.CrossingNotifierScryId })
					.ToList();
				if (notifiers.Count == 0)
					continue;

				var candidates = notifiers
					.Select(a => (Area: a, Ids: RegionForms(a.Name)
						.SelectMany(f => byText.GetValueOrDefault(Normalise(f)) ?? [])
						.Distinct()
						.OrderBy(id => id)
						.ToList()))
					.ToList();

				var captured = capturedByMap.GetValueOrDefault(mapId) ?? [];
				var anchors = candidates
					.Where(c => c.Ids.Count == 1)
					.Select(c => c.Ids[0])
					.Concat(captured)
					.Distinct()
					.OrderBy(id => id)
					.ToList();
				if (anchors.Count == 0)
					continue;

				var blocks = Blocks(anchors, captured);
				var unmatched = new List<BoundedAreaCollection.BoundedArea>();
				foreach (var (area, ids) in candidates)
				{
					var inBlock = ids.Where(id => blocks.Any(b => id >= b.Lo && id <= b.Hi)).ToList();

					// A CAPTURED id among the survivors wins outright. The block is a proximity argument and the wire is
					// a fact: retail sent this id on this playfield, so if one of the candidates is that id, the choice
					// is already made. This is what settles the generic names on the playfields we have walked -
					// "Outlaw Camp" on Old Tarantia has 3906077 and 3906603 in block, and 3906077 is the one retail
					// actually sent.
					var wire = inBlock.Where(captured.Contains).ToList();
					if (wire.Count == 1)
						inBlock = wire;

					if (inBlock.Count == 1)
					{
						matched.Add(new Match(mapId, area.Name, inBlock[0], textById[inBlock[0]]));
						continue;
					}

					if (inBlock.Count > 1)
						notes.Add($"banner region '{area.Name}' on map {mapId}: {inBlock.Count} recovered ids of that name sit in this playfield's blocks ({string.Join(", ", inBlock)}) - left unresolved");
					else if (ids.Count > 0)
						notes.Add($"banner region '{area.Name}' on map {mapId}: {ids.Count} recovered id(s) bear that text but none is in this playfield's id blocks - left unresolved");
					else
						unmatched.Add(area);
				}

				matched.AddRange(MatchAbbreviations(mapId, unmatched, LooseIds(blocks, banners, matched, mapId), textById, notes));
			}

			return matched;
		}

		// The ids inside this playfield's blocks that no region has claimed - the only pool the abbreviation pass below
		// is allowed to draw from. Every one of them is already known to be this playfield's own.
		private static List<uint> LooseIds(
			List<(uint Lo, uint Hi)> blocks,
			IReadOnlyList<Banner> banners,
			List<Match> matched,
			uint mapId)
		{
			var taken = matched.Where(m => m.MapId == mapId).Select(m => m.AreaNameId).ToHashSet();
			return banners
				.Select(b => b.Id)
				.Distinct()
				.Where(id => !taken.Contains(id) && blocks.Any(b => id >= b.Lo && id <= b.Hi))
				.OrderBy(id => id)
				.ToList();
		}

		// THE LAST PASS, and deliberately the last: the client sometimes ABBREVIATES a word in the internal name that
		// the banner spells out, or writes only the distinctive part of it.
		//
		//   `BA_AVRoses`   -> "Avenue of Roses"        `BA_AVLions` -> "Avenue of Lions"
		//   `BA_AVHonor`   -> "Avenue of Honor"        `BA_Docks`   -> "Old Tarantia Docks"
		//
		// This is the only rule here that is not an exact identity, so it is fenced on all four sides:
		//
		//   * the id must already be in one of the PLAYFIELD's blocks and unclaimed - it is this playfield's own id,
		//     the only question is which of its regions it belongs to;
		//   * the region must have matched NOTHING by name, so this can never overrule an identity;
		//   * every word of the region name must be a PREFIX of a word of the text, in order (`AV` of `Avenue`,
		//     `Docks` of `Docks`) - a region word that appears nowhere in the text kills the pair;
		//   * the pairing must be unique in BOTH directions among what is left.
		//
		// On Old Tarantia that is a complete bijection - 17 regions against the 17 ids of its blocks, every one paired -
		// which is the real check on it: a wrong pairing would have to be a permutation that satisfies the rule
		// everywhere, and there is none.
		private static List<Match> MatchAbbreviations(
			uint mapId,
			List<BoundedAreaCollection.BoundedArea> unmatched,
			List<uint> loose,
			Dictionary<uint, string> textById,
			List<string> notes)
		{
			var pairs = new List<(BoundedAreaCollection.BoundedArea Area, uint Id)>();
			foreach (var area in unmatched)
			foreach (var id in loose)
			{
				if (RegionForms(area.Name).Any(f => IsAbbreviationOf(Words(f), Words(textById[id]))))
					pairs.Add((area, id));
			}

			var matched = new List<Match>();
			foreach (var (area, id) in pairs)
			{
				if (pairs.Count(p => p.Area == area) > 1 || pairs.Count(p => p.Id == id) > 1)
				{
					notes.Add($"banner region '{area.Name}' on map {mapId}: '{textById[id]}' ({id}) abbreviates to it, but the pairing is not unique - left unresolved");
					continue;
				}

				// Reported even though it SUCCEEDED - this is the one rule in here that is not an exact identity, so
				// every row it produces should be readable without re-deriving it.
				notes.Add($"banner region '{area.Name}' on map {mapId}: abbreviates '{textById[id]}' ({id}) - taken, uniquely, from this playfield's unclaimed ids");
				matched.Add(new Match(mapId, area.Name, id, textById[id]));
			}

			return matched;
		}

		// Whether every word of `name` is a prefix of a word of `text`, in order. Stopwords are dropped from the TEXT
		// only - the internal name never carries them.
		private static bool IsAbbreviationOf(List<string> name, List<string> text)
		{
			if (name.Count == 0 || text.Count == 0)
				return false;

			var at = 0;
			foreach (var word in name)
			{
				while (at < text.Count && !text[at].StartsWith(word, StringComparison.OrdinalIgnoreCase))
					at++;

				if (at++ == text.Count)
					return false;
			}

			return true;
		}

		// A name split into words: on separators AND on camel case, so `BA_AVRoses` reads as [AV, Roses] and
		// `GreenManTavern` as [Green, Man, Tavern]. An uppercase RUN keeps its last letter for the word that follows
		// it, which is what makes `AVRoses` split in the middle rather than at the end.
		private static List<string> Words(string name)
		{
			var stripped = LeadingArticle.Replace(WidgetPrefix.Replace(name.Trim(), ""), "");
			return CamelBoundary
				.Replace(stripped, " $1$2")
				.Split([' ', '\t', '_', '-', '\'', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries)
				.Where(w => !Stopwords.Contains(w.ToLowerInvariant()))
				.ToList();
		}

		private static readonly Regex CamelBoundary = new Regex(@"(?<=[a-z0-9])([A-Z])|(?<=[A-Z])([A-Z][a-z])", RegexOptions.Compiled);
		private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "the", "of", "a", "an", "and", "s" };

		// The playfield's id blocks: anchors clustered by BlockGap, widened by BlockMargin.
		//
		// Two ways a cluster fails to be a block, and both are the same mistake seen from different sides - another
		// playfield's copy of these places:
		//
		//   * a ONE-ANCHOR cluster, unless that anchor is CAPTURED. A lone name match is exactly the case that turns
		//     out to be somewhere else of the same name (map 1010's "Cimmerian Settlement" at 711411).
		//   * ANY cluster with no captured anchor, once the playfield has captured ids at all. The wire has then
		//     already said where this playfield's numbering lives, and a name-derived cluster elsewhere is a copy
		//     (Ymir's Pass: eight regions name-match 3908476..3908749 while retail was captured sending 269xxx).
		private static List<(uint Lo, uint Hi)> Blocks(List<uint> anchors, IReadOnlyCollection<uint> captured)
		{
			var blocks = new List<(uint Lo, uint Hi)>();
			var run = new List<uint> { anchors[0] };
			foreach (var id in anchors.Skip(1))
			{
				if (id - run[^1] <= BlockGap)
				{
					run.Add(id);
					continue;
				}

				Close(run);
				run = [id];
			}

			Close(run);
			return blocks;

			void Close(List<uint> ids)
			{
				var pinned = ids.Any(captured.Contains);
				if (!pinned && (ids.Count < 2 || captured.Count > 0))
					return;

				blocks.Add((ids[0] - Math.Min(ids[0], BlockMargin), ids[^1] + BlockMargin));
			}
		}

	}
}
