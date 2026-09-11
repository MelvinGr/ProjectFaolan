using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report morph-stats`: does retail ever push a shapeshift stat on the wire, and if so how?
	//
	// The polymorph family is the `SpellStatEffects` rows whose stats are appearance fields - `Monster Data` (359),
	// `Head Mesh` (64), `Scale` (360), `Hair Render Type` (47), `Super Family` (121), `BCC` (21), `Char Radius` (421),
	// `Race` (89), `Side` (33). What the server sends when a player morphs is not otherwise recorded, so the answer
	// has to come from the corpus or not at all.
	//
	// The candidate needs no new packet: every one of those is an ordinary wire stat id, and `StatIIR`
	// (0xF98E10B3) sets one stat to one value on one entity. So the question is answerable by census: over every
	// captured `StatIIR`, does the morph set ever appear, on what entity class, and with what values.
	//
	// A NEGATIVE is the useful answer here too, and it has to be stated as one: "retail never pushes these"
	// closes off inventing a StatIIR morph, which is exactly what the project's rule forbids. So the report prints
	// the whole stat census beside it - without that, "the morph stats are absent" is indistinguishable from "the
	// scan is broken".
	public static partial class ReportCli
	{
		// The stats a `TempChangeSkill` (53110) shapeshift row sets, from `--report spell-effect-gap 53110`.
		private static readonly (uint Id, string Name)[] MorphStats =
		[
			(21, "BCC"), (33, "Side"), (47, "Hair Render Type"), (64, "Head Mesh"), (89, "Race"),
			(121, "Super Family"), (359, "Monster Data"), (360, "Scale"), (421, "Char Radius")
		];

		private static void MorphStatsReport(IReadOnlyList<NamedCapture> captures)
		{
			var perStat = new Dictionary<uint, List<(string Capture, uint EntityClass, uint Handle, uint Value)>>();
			var total = 0;
			foreach (var capture in captures)
			foreach (var p in capture.Packets)
			{
				if (p is not { IsIirCarrier: true, SubOpcode: StatCodec.Opcode } || !p.TryParse(StatCodec.Parse, out var stat))
					continue;

				total++;
				if (!perStat.TryGetValue(stat.StatId, out var rows))
					perStat[stat.StatId] = rows = [];

				rows.Add((capture.Name, stat.EntityClass, stat.Handle, stat.Value));
			}

			Console.WriteLine($"{total:N0} captured StatIIR records over {captures.Count} captures, {perStat.Count} distinct stat ids.");
			Console.WriteLine();
			Console.WriteLine("the SHAPESHIFT set (#709) - does retail ever push one?");
			var seen = 0;
			foreach (var (id, name) in MorphStats)
			{
				if (!perStat.TryGetValue(id, out var rows))
				{
					Console.WriteLine($"  {id,-5} {name,-20} ABSENT");
					continue;
				}

				seen++;
				var classes = rows.Select(r => $"0x{r.EntityClass:X4}").Distinct().Order();
				var values = rows.Select(r => r.Value).Distinct().Order().Take(8);
				Console.WriteLine($"  {id,-5} {name,-20} {rows.Count,5} record(s)  classes={string.Join(",", classes)}  values={string.Join(",", values)}");
				foreach (var g in rows.GroupBy(r => r.Capture).OrderByDescending(g => g.Count()).Take(4))
					Console.WriteLine($"        {g.Count(),4} in {g.Key}  handles={g.Select(r => $"0x{r.Handle:X}").Distinct().Count()}");
			}

			Console.WriteLine();
			Console.WriteLine(seen == 0
				? "  ⛔ NONE of them. Retail never pushes an appearance stat through StatIIR in this corpus, so a StatIIR\n"
				  + "     morph would be a packet retail does not send - see the census below for what it DOES push."
				: $"  {seen} of {MorphStats.Length} appear - the wire form is StatIIR and needs no new message.");

			Console.WriteLine();
			Console.WriteLine("the whole census, most-pushed first (the control: an empty morph row above is a real absence, not a broken scan):");
			foreach (var (id, rows) in perStat.OrderByDescending(kv => kv.Value.Count).Take(30))
				Console.WriteLine($"  {id,-6} 0x{id:X3}  {rows.Count,6} record(s)  {rows.Select(r => $"0x{r.EntityClass:X4}").Distinct().Count()} entity class(es)");
		}
	}
}
