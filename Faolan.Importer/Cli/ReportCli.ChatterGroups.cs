using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report chatter-groups`: recover the ambient-bark conversation group names, by restoring the CamelCase
	// the client's name table folds away and testing each casing against the bark catalog.
	//
	// ## Why a search, and why this one is sound
	//
	// A bark's string id is `elf28("sayinchat(N)|<channel>(K)|<Group>")` - the whole token, case-sensitively. The
	// group names are enumerable: every known one is referenced by `1020004/1010029`, the server-side SCRIPT
	// catalog's name list, which is 5,810 bare identifiers. But that table is LOWERCASED by the build, and
	// `elf28("...|renownbystanderlovers")` resolves to nothing while `...|RenownBystanderLovers` resolves to
	// *"That's just dreadful, Lord Ovidius, really."*. So the pool is exact and the casing is what is missing.
	//
	// The obvious acceptance test does not work, and this report is what measures that. Accepting a casing when two
	// or more `sayinchat(N)` values resolve for it looks safe - one 28-bit collision is ~5e-6 and two is ~3e-9 - but
	// that argument assumes the hits are independent and they are not: the sweep gives
	// every (casing, channel, K) 256 chances - one per N - against a category whose 1,276 ids are themselves clustered
	// (an ELF hash's low bits depend only on the last few characters, and every id of one group shares a suffix).
	//
	// Measured over the full sweep: 350 of the 781 pool names that pass the two-hit rule pass it under more than one
	// mutually-contradictory casing - `arenasendaggromessage` under twelve. `AchievementsPetHeraldOfDeeds` (correct)
	// and `AchievementsPetHeRaLdoFdeEds` (obvious nonsense) both "resolve" nine or more lines. So a hit count is not
	// evidence of a casing, and any number this report printed as "N groups recovered" would be fiction.
	//
	// Ranking by hit count does not rescue it either. The two known groups this vocabulary can reach do
	// come first - `RenownCrier sendtell(3)` at 18 lines and `AchievementsPetHeraldOfDeeds broadcastcycle(6)` at 13,
	// both matching their captured `NpcChatter` rows exactly - but the top of the overall ranking is nonsense:
	//
	//     halloweensnakebosscombat   ->  HalloweenSnakeBoSscOmbAt   24 lines
	//     playerlocalteleport        ->  PlayerLocalTeLePoRt        22 lines
	//
	// And the control makes it conclusive rather than merely suspicious: for `renownbystanderlovers`, whose true
	// casing a capture states, the sweep accepts a casing and it is not the true one - because "bystander" is not a
	// word the vocabulary can supply, so `RenownBystanderLovers` is never even tried, while some other split
	// of the same letters resolves enough lines to pass. A method that returns a confident wrong answer for the one
	// row whose answer is known is not a method.
	//
	// The mechanism, so the next attempt does not re-derive it: the ELF-28 hash is `h = (h << 4) + c` folded, so
	// its low bits depend only on the last few characters. Tokens that share a suffix - which every candidate for one
	// group does - land in the same narrow band. Printing four of them makes it visible at a glance:
	//
	//     sayinchat(137)|broadcastcycle(6)|Achievements...  ->  0x0a93eb03
	//     sayinchat(138)|broadcastcycle(6)|Achievements...  ->  0x0d93eb03
	//
	// The effective space is far smaller than 2^28, so a hit is cheap and 2.5e9 probes buy a great many of them.
	//
	// So this report imports nothing and asserts nothing. It prints a ranked candidate list, the contested-casing
	// rate, and the control - and its value is the negative: it closes the "restore the CamelCase and sweep" route,
	// with the numbers that close it.
	//
	// ## The casing comes from segmentation, and the vocabulary is the client's own
	//
	// `renownbystanderlovers` is `Renown|Bystander|Lovers`. Segmenting needs an English vocabulary, and the one that
	// works is the client's own LDB text - the same words the game says out loud. A vocabulary built from the
	// filename table's `_`-separated parts was tried first and gets 1 of the 5 known groups; this one is built from
	// every English LDB page.
	//
	// A segmentation is a hypothesis, and the report tries every one it can find. What the measurements above show is
	// that the hash cannot then decide between them, which is the load-bearing assumption the approach needs.
	//
	// And the vocabulary has a hard floor no client-derived source can lift: `bystander` occurs nowhere in the
	// client - not in the 152 English LDB pages, not in any of the 192,291 name-table entries - except inside the
	// four `renownbystander*` names themselves. An authoring word the game never says is unreachable by construction,
	// so four of the six known groups can never be split from client data alone, whatever the acceptance test.
	public static partial class ReportCli
	{
		private const uint BarkTextCategory = 50015;
		private const uint LdbType = 1030002;

		// The channels and per-channel counts the captured barks use. The token's shape is
		// `sayinchat(N)|<channel>(K)|<Group>`, and both K and the channel are properties of the group - so a sweep has
		// to try each pairing rather than assume the one a given group was captured with.
		private static readonly string[] BarkChannels =
			["conversation1", "conversation2", "conversation3", "conversation4", "broadcastcycle", "event", "eventactions", "sendtell"];

		// The per-channel counts the captured barks use (2, 3, 4, 6) plus the gaps between them. K is small - it is
		// how many lines the channel has - and every extra value multiplies the prefix table, so this is the observed
		// set widened by one step rather than an open range.
		private static readonly uint[] ChannelCounts = [1, 2, 3, 4, 5, 6, 7, 8];

		// How far to sweep N. The captured maximum is 138 (`AchievementsPetHeraldOfDeeds`); 256 is generous, and each
		// extra value is one more precomputed prefix rather than a re-hash, so the width is cheap.
		private const uint MaxSayInChat = 256;

		// The longest split worth trying. The real names run longer than they look -
		// `CityLife|Cimmerian|Child|React|To|Player` is seven words - and iterative deepening means a high cap costs
		// nothing on the names that split short, because those are found and counted at depth 2 or 3 first.
		private const int MaxWords = 8;

		// How many segmentations to try per name. The DP enumerates shortest-first, and a name whose right split is
		// not in the first few is one the vocabulary cannot express anyway.
		private const int MaxSegmentationsPerName = 40;

		private static void ChatterGroups(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);

			// The bark catalog: id -> text, English pages of category 50015 only.
			var barks = new Dictionary<uint, string>();
			var vocabulary = new HashSet<string>(StringComparer.Ordinal);
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);

			foreach (var e in rdb.EntriesOfType(LdbType).Where(rdb.CanRead))
			{
				if (!languageByPage.TryGetValue(e.Id, out var language) || language != LdbLanguageIndex.English)
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

				var category = LdbStringIndex.CategoryOf(body);
				foreach (var s in LdbStringIndex.Parse(body))
				{
					if (string.IsNullOrWhiteSpace(s.Value))
						continue;

					if (category == BarkTextCategory)
						barks[s.Id] = s.Value;

					// The VOCABULARY, from every English page rather than only the barks: the group names are
					// authoring identifiers, so their words come from the game's whole world - `Bystander`, `Crier`,
					// `Stygian`, `Distress` - not from the lines themselves.
					foreach (var word in Words(s.Value))
						vocabulary.Add(word);
				}
			}

			Console.WriteLine($"chatter-groups: LDB[{BarkTextCategory}] carries {barks.Count:N0} bark string(s); "
				+ $"vocabulary of {vocabulary.Count:N0} word(s) from every English page.");

			// The second vocabulary source, and the one that reaches AUTHORING words: the name table's own FILENAMES
			// (187k of them), split on their `_` and other separators. `spawner`, `timer`, `brainscript`, `citylife`
			// are words the game never says out loud but the content team names files with, and a group name is an
			// authoring identifier before it is anything else.
			//
			// It does not rescue every name, and the limit is measured rather than assumed - see the report's
			// closing note on `bystander`.
			foreach (var name in RdbNameTable.Load(rdb).Values)
			foreach (var word in Words(name))
				vocabulary.Add(word);

			Console.WriteLine($"  vocabulary grows to {vocabulary.Count:N0} with the name table's filename parts.");

			var pool = ScriptNames(rdb);
			Console.WriteLine($"  pool: {pool.Count:N0} bare identifier(s) from 1020004/1010029 (the server-side script catalog).");

			if (barks.Count == 0 || pool.Count == 0)
			{
				Console.WriteLine("  ⛔ nothing to sweep - the RDB is missing one of the two halves.");
				return;
			}

			// The sweep's shape is the whole cost. The group name is the token's suffix, so the prefix
			// `sayinchat(N)|<channel>(K)|` does not depend on the candidate: it is precomputed once - a few thousand
			// resumable hash states - and each casing is then hashed onto each state. Written the other way round
			// (prefix rebuilt inside the N loop) the same sweep re-hashes a 35-char tail a few billion times and does
			// not finish; this way it is one pass of `casings x states` over ~20 chars each.
			var prefixes = new List<(uint State, string Channel, uint K, uint N)>();
			var sayInChat = Elf28.Of("sayinchat(", 0);
			foreach (var channel in BarkChannels)
			foreach (var k in ChannelCounts)
			{
				for (uint n = 1; n <= MaxSayInChat; n++)
				{
					var state = Elf28.OfDigits(n, sayInChat);
					state = Elf28.Of($")|{channel}({k})|".AsSpan(), state);
					prefixes.Add((state, channel, k, n));
				}
			}

			Console.WriteLine($"  {prefixes.Count:N0} token prefix(es) precomputed ({BarkChannels.Length} channels x "
				+ $"{ChannelCounts.Length} counts x {MaxSayInChat} sayinchat values).");

			var found = new List<(string Name, string Casing, string Channel, uint K, List<(uint N, string Text)> Lines)>();
			var tried = 0L;

			foreach (var name in pool)
			{
				foreach (var casing in Casings(name, vocabulary))
				{
					tried++;

					// (channel, k) -> the N values that resolved. Collected across the whole prefix list, because the
					// two-hit rule is per (channel, k) rather than per probe.
					Dictionary<(string Channel, uint K), List<(uint N, string Text)>>? hits = null;
					foreach (var (state, channel, k, n) in prefixes)
					{
						if (!barks.TryGetValue(Elf28.Of(casing.AsSpan(), state), out var text))
							continue;

						hits ??= [];
						var key = (channel, k);
						(hits.TryGetValue(key, out var list) ? list : hits[key] = []).Add((n, text));
					}

					if (hits == null)
						continue;

					// Two or more, never one - see the header. This is the whole soundness argument.
					foreach (var ((channel, k), lines) in hits.Where(h => h.Value.Count >= 2))
						found.Add((name, casing, channel, k, lines));
				}
			}

			Console.WriteLine($"  swept {tried:N0} casing(s), {tried * prefixes.Count:N0} probe(s).");
			Console.WriteLine();

			if (found.Count == 0)
			{
				Console.WriteLine("  ⛔ no casing resolved two or more lines - the vocabulary cannot reach any split, or the");
				Console.WriteLine("     token shape is not what this sweep assumes. Both are checkable against the groups already");
				Console.WriteLine("     in NpcChatter, which this sweep should rediscover.");
				return;
			}

			// Ranked per POOL name, because that - not the casing - is the thing the client's own catalog names. The
			// runner-up with a different casing is printed beside each winner: that margin is the only honest measure
			// of how much the top row is worth, and where it is thin the answer is a coin toss.
			var byPool = found.GroupBy(f => f.Name).OrderByDescending(g => g.Max(f => f.Lines.Count)).ToList();
			var contested = byPool.Count(g => g.Select(f => f.Casing).Distinct().Count() > 1);

			Console.WriteLine($"  {found.Count:N0} (casing, channel, count) combination(s) over {byPool.Count:N0} pool name(s).");
			Console.WriteLine($"  ⛔ {contested:N0} of those pool names ({100.0 * contested / byPool.Count:F0}%) pass under MORE THAN ONE casing -");
			Console.WriteLine("     which is why the two-hit rule is not an acceptance test and this is a RANKING, not an answer.");
			Console.WriteLine();
			Console.WriteLine($"  {"pool name",-38} {"top casing",-38} {"channel",-18} {"lines",5}  runner-up");
			foreach (var group in byPool.Take(60))
			{
				var best = group.OrderByDescending(f => f.Lines.Count).First();
				var rival = group.Where(f => f.Casing != best.Casing).OrderByDescending(f => f.Lines.Count).FirstOrDefault();
				Console.WriteLine($"  {Trim(group.Key, 38),-38} {Trim(best.Casing, 38),-38} {$"{best.Channel}({best.K})",-18} {best.Lines.Count,5}"
					+ (rival.Casing == null ? "  (uncontested)" : $"  {Trim(rival.Casing, 30)} @{rival.Lines.Count}"));
			}

			if (byPool.Count > 60)
				Console.WriteLine($"  ... and {byPool.Count - 60:N0} more pool name(s).");

			// The control, printed rather than described: the groups already in NpcChatter are the only rows whose
			// answer is known, so the report scores itself against them every run. A change that breaks the ranking
			// shows up here rather than in a number that merely got bigger.
			Console.WriteLine();
			Console.WriteLine("  ⭐ CONTROL - the known groups, and where this sweep ranks their true casing:");
			foreach (var known in KnownGroups)
			{
				var group = byPool.FirstOrDefault(g => string.Equals(g.Key, known, StringComparison.OrdinalIgnoreCase));
				if (group == null)
				{
					Console.WriteLine($"      {known,-38} NOT REACHED - the vocabulary cannot split it");
					continue;
				}

				var ranked = group.OrderByDescending(f => f.Lines.Count).ToList();
				var at = ranked.FindIndex(f => f.Casing == known);
				Console.WriteLine($"      {known,-38} {(at < 0 ? "casing never resolved" : $"rank {at + 1} of {ranked.Count}, {ranked[at].Lines.Count} lines, {ranked[at].Channel}({ranked[at].K})")}");
			}
		}

		// The groups already harvested into NpcChatter - the only ones whose true casing is known, so the only honest
		// control this sweep has.
		private static readonly string[] KnownGroups =
		[
			"RenownBystanderLovers", "RenownBystanderChild", "RenownBystanderWomen", "RenownCrier",
			"AchievementsPetHeraldOfDeeds", "CityLifeCimmerianChildReactToPlayer"
		];

		// The pool: the names an index record of type 1020004 references, bare identifiers only - a dot means a file
		// name. The same walk `--report rdb-names index:` does.
		private static List<string> ScriptNames(RdbReader rdb)
		{
			var names = RdbNameTable.Load(rdb);
			if (names.Count == 0 || !rdb.TryGet(NameIndexType, 1010029, out var entry) || !rdb.CanRead(entry))
				return [];

			var body = rdb.ReadDecompressed(entry);
			var referenced = new List<string>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			for (var at = 0; at + 4 <= body.Length; at += 4)
			{
				if (names.TryGetValue(body.ReadU32Le(at), out var name)
				    && !name.Contains('.', StringComparison.Ordinal)
				    && name.All(char.IsLetter)
				    && seen.Add(name))
					referenced.Add(name);
			}

			return referenced;
		}

		// Every CamelCase spelling of `name` its letters can be split into under `vocabulary`, shortest-first.
		//
		// The name itself capitalised is always tried first, because a one-word group (`Idar`, `Aratus`) has no
		// split to find and would otherwise be unreachable.
		private static IEnumerable<string> Casings(string name, HashSet<string> vocabulary)
		{
			yield return char.ToUpperInvariant(name[0]) + name[1..];

			var yielded = 1;
			foreach (var words in Segmentations(name, vocabulary))
			{
				if (yielded++ >= MaxSegmentationsPerName)
					yield break;

				yield return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
			}
		}

		// Splits `name` into vocabulary words, FEWEST words first - by iterative deepening on the word count rather
		// than by sorting whatever a capped DFS happened to find.
		//
		// That distinction is not cosmetic. A plain depth-first search with a results cap fills its budget with the
		// deep splits it reaches first (`re|now|n|by|stander|lovers`) and never reaches the two-word ones, so a
		// canonical split like `Renown|Bystander|Lovers` is cut off entirely. Sorting the results afterwards cannot
		// fix that: the right answer was never in the list.
		private static IEnumerable<List<string>> Segmentations(string name, HashSet<string> vocabulary)
		{
			var results = new List<List<string>>();
			for (var depth = 2; depth <= MaxWords && results.Count < MaxSegmentationsPerName; depth++)
				Walk(0, [], depth);

			return results;

			void Walk(int at, List<string> acc, int depth)
			{
				if (results.Count >= MaxSegmentationsPerName)
					return;

				if (at == name.Length)
				{
					if (acc.Count == depth)
						results.Add([.. acc]);

					return;
				}

				// No budget left for another word, or not enough characters left to fill the remaining ones.
				if (acc.Count >= depth || name.Length - at < depth - acc.Count)
					return;

				// Longest word first: the right split is nearly always the one with the longest words, so this finds
				// it early within a depth, even though the depth loop is what guarantees fewest-first across depths.
				for (var end = name.Length; end > at; end--)
				{
					var word = name[at..end];
					if (word.Length < 2 || !vocabulary.Contains(word))
						continue;

					acc.Add(word);
					Walk(end, acc, depth);
					acc.RemoveAt(acc.Count - 1);
				}
			}
		}

		// The words of one LDB string, lower-cased: letter runs of 2+, which is what a segment of an identifier can be.
		private static IEnumerable<string> Words(string text)
		{
			var start = -1;
			for (var i = 0; i <= text.Length; i++)
			{
				var letter = i < text.Length && char.IsLetter(text[i]) && text[i] < 128;
				if (letter && start < 0)
					start = i;
				else if (!letter && start >= 0)
				{
					if (i - start >= 2)
						yield return text[start..i].ToLowerInvariant();

					start = -1;
				}
			}
		}
	}
}
