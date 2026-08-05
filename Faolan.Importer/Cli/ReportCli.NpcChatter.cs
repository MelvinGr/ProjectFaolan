using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report npc-chatter` - what the ambient-bark catalog (#331) actually costs, measured rather than assumed.
	//
	// The open half of #331 was written as "a dedicated extraction pass: map each NPC type to its ambient conversation
	// GROUP and pull the group's line set out of the RDB". This report is the answer to whether that pass can exist.
	//
	// It can, and it rests on two facts that read the other way round at first glance:
	//
	//   1. **Category 50015 (`sayinchat`) is PLAINTEXT.** Its page header carries `flags=0`, not the `flags=1` of the
	//      eleven encrypted categories, so its strings need no key at all - the `key=` attribute a captured bark
	//      carries is not what unlocks the text. Every bark line the client ships is readable offline.
	//   2. **A bark's string id is COMPUTABLE from its token.** The id is the client's own 28-bit ELF hash of
	//      `sayinchat(N)|<channel>(K)|<Group>` (`LdbStringIndex.TokenId`), which is a pure function of values we
	//      already hold in `NpcChatter`. So a group's whole line set is enumerable by sweeping N.
	//
	// Together those turn the extraction pass from "needs a capture per line" into a sweep, which is what this report
	// does. What it prints:
	//   1. the page census for 50015 - pages, flags, distinct ids;
	//   2. every stored NpcChatter row resolved to its TEXT, the end-to-end proof that the token hash is right;
	//   3. per (group, channel) a SWEEP of N, listing every line the client ships for that group - the lines we hold
	//      no row for are the harvest;
	//   4. the coverage line - lines shipped vs lines we serve.
	//
	// ⚠️ What it deliberately does NOT do is DISCOVER group names. The obvious route - pull identifiers out of the
	// client's SCRIPTGROUP2 (1010028) records and hash-test each - was built and thrown away, because it does not
	// work and fails in a way that looks like success. None of the five groups the corpus already proves
	// (`RenownBystanderLovers`, `RenownCrier`, ...) appears anywhere in those 11,118 identifiers, so the route finds
	// no real group at all; what it does return is hash noise, and plenty of it. The id is 28 bits and each candidate
	// is probed thousands of times, but the probes are NOT independent - names differing only in a digit cluster in
	// ELF-hash space - so a "3+ hits" threshold that looks safe under a Poisson estimate still let ten confident
	// non-groups through (`CoyoteFactor`, `seagull_01_1shot`, `texscript_196962`). Group names live somewhere else.
	public static partial class ReportCli
	{
		// `sayinchat` - the ambient bark category every captured bark names in its `category=` attribute.
		private const uint SayInChatCategory = 50015;

		// A bark's template slot - `{TopGloryName}`, `{DetectedChar.name}`, `{AdvertiseMinutes - AdvertiseCounter}`.
		// A line carrying one cannot simply be served: the wire form needs a matching <parameter> element whose value
		// comes from live server state, which is a separate piece of work from harvesting the TEXT.
		private static readonly System.Text.RegularExpressions.Regex SlotRx = new System.Text.RegularExpressions.Regex("\\{[^}]+\\}", System.Text.RegularExpressions.RegexOptions.Compiled);

		// How far the per-group sweep walks `sayinchat(N)`. Empirical headroom: the highest N any capture carries is
		// 57 (Tatianus the Crier), and each candidate is one hash plus one dictionary probe.
		private const uint SweepCeiling = 512;


		private static async Task NpcChatter(string rdbDir)
		{
			var census = SayInChatPages(rdbDir);
			if (census.Pages == 0)
			{
				Console.WriteLine($"npc-chatter: no ENGLISH category-{SayInChatCategory} pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			Console.WriteLine($"category {SayInChatCategory} (sayinchat): {census.Pages} English page(s), flags={string.Join("/", census.Flags.OrderBy(f => f))}, {census.Blobs.Count} distinct string ids");
			Console.WriteLine(census.Flags.Contains(1)
				? "  -> ENCRYPTED (flags=1): the key would be per string and arrive on the wire, making these unreadable offline."
				: "  -> PLAINTEXT (flags=0): no key needed. The bark catalog is harvestable (#331).");
			Console.WriteLine();

			await using var db = OpenDb();
			var rows = await db
				.NpcChatter.AsNoTracking()
				.Include(c => c.Parameters)
				.OrderBy(c => c.Group)
				.ThenBy(c => c.NpcTemplateId)
				.ThenBy(c => c.Channel)
				.ThenBy(c => c.SayInChat)
				.ToListAsync();

			var names = await db.NpcTemplates.AsNoTracking().ToDictionaryAsync(t => t.TemplateId, t => t.Name);

			Console.WriteLine("=== the barks we hold a key for ===");
			var decrypted = 0;
			foreach (var row in rows)
			{
				var token = $"sayinchat({row.SayInChat})|{row.Channel}({row.ChannelCount})|{row.Group}";
				var id = LdbStringIndex.TokenId(token);
				names.TryGetValue(row.NpcTemplateId, out var npc);

				var text = Text(census, id);
				if (text != null)
					decrypted++;

				Console.WriteLine($"  {npc ?? "?",-24} {token}");
				Console.WriteLine($"      id {id,-12} {(text != null ? "\"" + text + "\"" : "<NOT IN THE PAGE INDEX>")}");
			}

			// The sweep. A group's lines are `sayinchat(N)` for successive N against the SAME channel(count) suffix, so
			// every (channel, count, group) triple we have ever seen is swept over N. The ceiling is empirical: the
			// highest N any capture carries is 57 (the crier), and 512 leaves an order of magnitude of headroom while
			// staying instant - a hash lookup per candidate.
			Console.WriteLine();
			Console.WriteLine("=== the full line set the client ships for each captured group ===");
			var held = rows.Select(r => (r.Group, r.Channel, r.ChannelCount, r.SayInChat)).ToHashSet();
			var found = 0;
			var servable = 0;
			var needsSlots = 0;
			foreach (var suffix in rows.Select(r => (r.Group, r.Channel, r.ChannelCount)).Distinct().OrderBy(x => x.Group).ThenBy(x => x.Channel))
			{
				var lines = new List<(uint N, string Text)>();
				for (var n = 0u; n <= SweepCeiling; n++)
				{
					var id = LdbStringIndex.TokenId($"sayinchat({n})|{suffix.Channel}({suffix.ChannelCount})|{suffix.Group}");
					if (Text(census, id) is { Length: > 0 } line)
						lines.Add((n, line));
				}

				found += lines.Count;
				Console.WriteLine($"  {suffix.Group}/{suffix.Channel}({suffix.ChannelCount}): {lines.Count} line(s)");
				foreach (var (n, line) in lines)
				{
					var have = held.Contains((suffix.Group, suffix.Channel, suffix.ChannelCount, n));
					var slots = SlotRx.Matches(line).Select(m => m.Value).Distinct().ToList();
					if (!have && slots.Count == 0)
						servable++;
					else if (!have)
						needsSlots++;

					Console.WriteLine($"      [{n,3}] {(have ? "have" : slots.Count == 0 ? "NEW " : "SLOT")}  \"{line}\"{(slots.Count > 0 ? "   <- " + string.Join(" ", slots) : "")}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("=== coverage ===");
			Console.WriteLine($"  {rows.Count} stored bark(s) across {rows.Select(r => r.Group).Distinct().Count()} group(s) and {rows.Select(r => r.NpcTemplateId).Distinct().Count()} NPC template(s); {decrypted} resolve to text.");
			Console.WriteLine($"  the sweep finds {found} line(s) for those groups - {found - rows.Count} the DB does not carry yet.");
			Console.WriteLine($"  of those: {servable} carry NO template slot and can be served as-is; {needsSlots} contain a {{Slot}} the server would have to fill.");
			Console.WriteLine($"  the client ships {census.Blobs.Count} strings in this category, so the reach beyond the captured groups is finding more GROUP names (#331).");
		}

		// The page's string, NUL-truncated and UTF-8 decoded - the plaintext form. Null when the id is not in the page
		// or the bytes are not valid UTF-8 (which would mean the category is not plaintext after all).
		private static string? Text((int Pages, HashSet<uint> Flags, Dictionary<uint, byte[]> Blobs) census, uint id)
		{
			if (!census.Blobs.TryGetValue(id, out var blob))
				return null;

			var end = Array.IndexOf(blob, (byte)0);
			var span = blob.AsSpan(0, end < 0 ? blob.Length : end);
			try
			{
				return new System.Text.UTF8Encoding(false, true).GetString(span);
			}
			catch (System.Text.DecoderFallbackException)
			{
				return null;
			}
		}

		// The English pages of the bark category: how many, what their encryption flag says, and their raw blobs by id.
		private static (int Pages, HashSet<uint> Flags, Dictionary<uint, byte[]> Blobs) SayInChatPages(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var blobs = new Dictionary<uint, byte[]>();
			var flags = new HashSet<uint>();
			var pages = 0;

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(x => x.IsLocal))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not a readable record (CDN-only / not zlib)
				}

				if (LdbStringIndex.CategoryOf(body) != SayInChatCategory)
					continue;

				pages++;
				flags.Add(LdbStringIndex.FlagsOf(body));
				foreach (var entry in LdbStringIndex.ParseRaw(body))
					blobs[entry.Id] = entry.Bytes;
			}

			return (pages, flags, blobs);
		}
	}
}
