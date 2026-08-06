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

		// The client's region name reduced to what it and the banner text have in common. Three things differ between
		// the two spellings and none of them is a different place:
		//
		//   * the widget prefix - `BA_Bridge`, `Fifo-CascadeFalls`, `BA_FIFO_ValleyOfGiants` (FIFO is the client's own
		//     name for the banner widget, so a region named after one says so);
		//   * word separation - the region is written `CrawlingDunes` or `Zang Xin Camp`, the text `Crawling Dunes`;
		//   * punctuation - `Atzels Approach` against `Atzel's Approach`, `Souleaters Pass` against `Souleaters' Pass`.
		//
		// So: drop the prefix, then keep the letters and digits. That is a normalisation, not a fuzzy match - two
		// different places never collapse together under it, and every pair it joins is the same string of words.
		public static string Normalise(string name)
		{
			var stripped = WidgetPrefix.Replace(name.Trim(), "");
			return new string(stripped.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
		}

		private static readonly Regex WidgetPrefix = new(@"^(BA[_-])?(FIFO[_-])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
		// A region with SEVERAL surviving candidates is left unresolved and reported, never guessed at.
		public static List<Match> MatchRegions(
			IReadOnlyDictionary<uint, List<BoundedAreaCollection.BoundedArea>> areasByMap,
			IReadOnlyList<Banner> banners,
			IReadOnlyDictionary<uint, IReadOnlyCollection<uint>> capturedByMap,
			List<string> notes)
		{
			var byText = banners
				.GroupBy(b => Normalise(b.Text))
				.ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());
			var textById = banners.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Text);

			var matched = new List<Match>();
			foreach (var (mapId, areas) in areasByMap.OrderBy(kv => kv.Key))
			{
				var notifiers = areas
					.Where(a => a.IsPolygon && a.ScryId == BoundedAreaClassifier.CrossingNotifierScryId)
					.ToList();
				if (notifiers.Count == 0)
					continue;

				var candidates = notifiers
					.Select(a => (Area: a, Ids: byText.GetValueOrDefault(Normalise(a.Name)) ?? []))
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
				}
			}

			return matched;
		}

		// The playfield's id blocks: anchors clustered by BlockGap, widened by BlockMargin. A one-anchor cluster only
		// survives if that anchor is CAPTURED - a lone name match is exactly the case that turns out to be another
		// playfield's place of the same name.
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
				if (ids.Count < 2 && !captured.Contains(ids[0]))
					return;

				blocks.Add((ids[0] - Math.Min(ids[0], BlockMargin), ids[^1] + BlockMargin));
			}
		}

	}
}
