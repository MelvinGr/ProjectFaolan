using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report world-object-items`: which world objects point at an item template we cannot hand over, and WHY.
	//
	// A quest-pickup node and a harvest node both pay out an `Items` row. When there is no such row the interaction
	// still runs - the node fades, regrows, and the player gets nothing - which is exactly how Gitea #824 presented
	// ('Spool of Thread' harvests but grants nothing). The listener now refuses that grant out loud, but the
	// underlying question is a data one and belongs in a report (#729): shipped data on both sides.
	//
	// ⭐ The split is the point, because only ONE half of it can ever be fixed by importing harder:
	//
	//   * **shipped, keyless** - the client carries the template in RDB 1000020/1000620 and we hold no TEA key for
	//     it. Dead end by construction (#203, closed won't-fix): the key is server-assigned, cryptographically
	//     random per item, and reaches us only by riding a `ProtoAcgItem` in some capture. There is no derivation and
	//     no key table, and `Items.Key1..4` are NOT NULL because the CLIENT needs the quad to decrypt the template
	//     from its own RDB - so a name-only row would be unusable rather than merely incomplete. These need a
	//     CAPTURE that references the item, which in practice means someone holding, looting or linking it.
	//   * **not shipped** - the client has no template either, so the reference is to something that is not an item
	//     template in this build. An import can never produce these and should not be blamed for them.
	public static partial class ReportCli
	{
		private static async Task<int> WorldObjectItems(string rdbDir)
		{
			await using var db = OpenDb();

			var held = (await db.Items.AsNoTracking().Select(i => i.Id).ToListAsync()).ToHashSet();
			var referenced = await db.WorldObjects.AsNoTracking()
				.Where(w => w.TemplateItemId != null)
				.Select(w => new
				{
					Template = w.TemplateItemId!.Value,
					w.Name,
					w.MapId
				})
				.ToListAsync();

			var missing = referenced.Where(r => !held.Contains(r.Template))
				.GroupBy(r => r.Template)
				.Select(g => new
				{
					Template = g.Key,
					Nodes = g.Count(),
					Names = g.Select(x => x.Name ?? "?").Distinct().OrderBy(x => x).ToList(),
					Maps = g.Select(x => x.MapId).Distinct().Count()
				})
				.OrderByDescending(x => x.Nodes)
				.ToList();

			Console.WriteLine($"{referenced.Count} world objects name an item template; {referenced.Select(r => r.Template).Distinct().Count()} distinct templates,"
			                  + $" of which {missing.Count} are absent from Items ({missing.Sum(m => m.Nodes)} objects).");

			if (missing.Count == 0)
			{
				Console.WriteLine("✅ every world object's item template is held.");
				return 0;
			}

			using var rdb = OpenRdb(rdbDir);
			var shipped = new List<string>();
			var absent = new List<string>();
			foreach (var m in missing)
			{
				var types = new uint[] { 1000020, 1000620 }.Where(t => rdb.TryGet(t, m.Template, out var e) && e.Size > 0).ToList();
				var line = $"  {m.Template,-10} {m.Nodes,4} node(s), {m.Maps} map(s)  {string.Join(" / ", m.Names.Take(3))}";
				(types.Count > 0 ? shipped : absent).Add(line);
			}

			Console.WriteLine($"\nSHIPPED BY THE CLIENT but keyless - needs a CAPTURE referencing the item, not an import ({shipped.Count} templates):");
			foreach (var line in shipped.Take(40))
				Console.WriteLine(line);
			if (shipped.Count > 40)
				Console.WriteLine($"  ... and {shipped.Count - 40} more");

			Console.WriteLine($"\nNOT SHIPPED - the client has no template either, so no import can produce these ({absent.Count} templates):");
			foreach (var line in absent.Take(40))
				Console.WriteLine(line);
			if (absent.Count > 40)
				Console.WriteLine($"  ... and {absent.Count - 40} more");

			// ⚠️ Reported, never asserted. Both populations are a fact about how much of the game the corpus has
			// touched, and that moves whenever captures are added - so a non-zero count here is not a defect. The
			// direction that WOULD be one (a node handing out an item we hold no row for, silently) is fixed in the
			// listener instead, where the grant now refuses out loud.
			return 0;
		}
	}
}
