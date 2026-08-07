using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report npc-chatter` - how far the ambient-bark catalog (#331) reaches, measured rather than assumed.
	//
	// The open half of #331 was written as "a dedicated extraction pass: map each NPC type to its ambient conversation
	// GROUP and pull the group's line set out of the RDB". This report is the answer to whether that pass can exist.
	// It can - `BarkCatalog` is it, and the two facts it turns on are documented there.
	//
	// What this prints:
	//   1. the page census for category 50015 - pages, flags, distinct ids;
	//   2. every stored NpcChatter row resolved to its TEXT, the end-to-end proof that the token hash is right;
	//   3. per (group, channel) a SWEEP of N listing every line the client ships, with the ones the DB lacks marked
	//      and split by whether they carry a {Slot} the server would have to fill;
	//   4. the coverage line.
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
		private static async Task NpcChatter(string rdbDir)
		{
			var catalog = BarkCatalog.Load(rdbDir, _options.RdbProxy ?? ImportContext.DefaultProxyBase(CliConfig.Resolve(_options.ConfigOverrides)));
			if (catalog.Pages == 0)
			{
				Console.WriteLine($"npc-chatter: no ENGLISH category-{BarkCatalog.Category} pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			Console.WriteLine($"category {BarkCatalog.Category} (sayinchat): {catalog.Pages} English page(s), flags={string.Join("/", catalog.Flags.OrderBy(f => f))}, {catalog.StringCount} distinct string ids");
			Console.WriteLine(catalog.IsPlaintext
				? "  -> PLAINTEXT (flags=0): no key needed. The bark catalog is harvestable (#331)."
				: "  -> ENCRYPTED (flags=1): the key would be per string and arrive on the wire, making these unreadable offline.");
			Console.WriteLine();

			await using var db = OpenDb();
			var rows = await db
				.NpcChatter.AsNoTracking()
				.OrderBy(c => c.Group)
				.ThenBy(c => c.NpcTemplateId)
				.ThenBy(c => c.Channel)
				.ThenBy(c => c.SayInChat)
				.ToListAsync();

			var names = await db.NpcTemplates.AsNoTracking().ToDictionaryAsync(t => t.TemplateId, t => t.Name);

			Console.WriteLine("=== every stored bark, resolved to its text ===");
			var resolved = 0;
			foreach (var row in rows)
			{
				names.TryGetValue(row.NpcTemplateId, out var npc);
				var text = catalog.Line(row.SayInChat, row.Channel, row.ChannelCount, row.Group);
				if (text != null)
					resolved++;

				Console.WriteLine($"  {npc ?? "?",-24} sayinchat({row.SayInChat})|{row.Channel}({row.ChannelCount})|{row.Group}");
				Console.WriteLine($"      {(text != null ? "\"" + text + "\"" : "<NOT IN THE PAGE INDEX>")}");
			}

			Console.WriteLine();
			Console.WriteLine("=== the full line set the client ships for each captured group ===");
			var held = rows.Select(r => (r.Group, r.Channel, r.ChannelCount, r.SayInChat)).ToHashSet();
			var found = 0;
			var servable = 0;
			var needsSlots = 0;
			foreach (var suffix in rows.Select(r => (r.Group, r.Channel, r.ChannelCount)).Distinct().OrderBy(x => x.Group).ThenBy(x => x.Channel))
			{
				var lines = catalog.Lines(suffix.Channel, suffix.ChannelCount, suffix.Group).ToList();
				found += lines.Count;
				Console.WriteLine($"  {suffix.Group}/{suffix.Channel}({suffix.ChannelCount}): {lines.Count} line(s)");
				foreach (var (n, line) in lines)
				{
					var have = held.Contains((suffix.Group, suffix.Channel, suffix.ChannelCount, n));
					var slots = BarkCatalog.SlotsOf(line);
					if (!have && slots.Count == 0)
						servable++;
					else if (!have)
						needsSlots++;

					Console.WriteLine($"      [{n,3}] {(have ? "have" : slots.Count == 0 ? "NEW " : "SLOT")}  \"{line}\"{(slots.Count > 0 ? "   <- " + string.Join(" ", slots) : "")}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("=== coverage ===");
			Console.WriteLine($"  {rows.Count} stored bark(s) across {rows.Select(r => r.Group).Distinct().Count()} group(s) and {rows.Select(r => r.NpcTemplateId).Distinct().Count()} NPC template(s); {resolved} resolve to text.");
			Console.WriteLine($"  the sweep finds {found} line(s) for those groups - {found - rows.Count} the DB does not carry yet.");
			Console.WriteLine($"  of those: {servable} carry NO template slot and are harvested by `--only npc-chatter`; {needsSlots} contain a {{Slot}} the server would have to fill.");
			Console.WriteLine($"  the client ships {catalog.StringCount} strings in this category, so the reach beyond the captured groups is finding more GROUP names (#331).");
		}
	}
}
