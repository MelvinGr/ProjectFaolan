using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Faolan.Importer.Cli
{
	// `--report bcc-trees`: the client's BEHAVIOUR CONTROL CENTRE trees, and the substate id -> name table the
	// BehaviorControlCenter IIR's phases 0x0B/0x0C carry on the wire (#810, #812).
	//
	// ⭐ **RDB 1010203 is plain-text XML** - there is nothing to decode. `BCCTree_c::Load` (`FUN_00603D70`) tries the
	// merged BINARY resource **1010501** (`0xF6B45`) first and falls back to the individual **1010203** (`0xF6A1B`)
	// entries, "falling back to text XML" in its own words; the two carry the same 29 trees and the text half reads
	// straight out:
	//
	//     <bctmesh name="Character">
	//       <substate id="84" name="Charged" priority="17"> <inherit group="misc" grouprdbid="3812796"/> </substate>
	//       <state    id="25" name="Bandaging">             <inherit group="death" grouprdbid="12474"/>  </state>
	//     </bctmesh>
	//
	// The `priority` is the field `FUN_005EB5C0` orders the active-substate list on (`entry + 0x24`).
	//
	// ⚠️ **It reports the id COLLISIONS, and that is the point of it being a report rather than a table.** The
	// client's own error says an id is resolved *"in this bcc"*, so the ids are per-tree by the rule. Measured over
	// the 29 shipped trees the two halves disagree: **substate** ids never collide (20 distinct, every shared id
	// carrying the same name AND the same priority in every tree that declares it), while **state** ids do - 11 is
	// `LoopingEmote` in one tree and `SneakHiding` in another. So a substate id->name map is publishable and a state
	// one is not, and the run PRINTS both counts rather than asserting either: a tree added by a patch could break
	// the substate half, and this is what would notice.
	public static partial class ReportCli
	{
		// The text-XML half of the BCC trees. The binary twin is 1010501, which parses to the same content.
		private const uint BccTreeTextType = 1010203;

		private static readonly Regex BctMeshName = new("<bctmesh name=\"([^\"]*)\"", RegexOptions.Compiled);

		// `priority` is present on substates and absent on states, so it is optional here rather than in two patterns.
		private static readonly Regex BctNode =
			new("""<(substate|state) id="(\d+)" name="([^"]*)"(?: priority="(\d+)")?""", RegexOptions.Compiled);

		private sealed record BctNodeRow(uint TreeId, string Tree, string Kind, int Id, string Name, int? Priority);

		private static void BccTrees(string rdbDir, string? lookup = null)
		{
			using var rdb = OpenRdb(rdbDir);

			var rows = new List<BctNodeRow>();
			var trees = 0;
			foreach (var entry in rdb.EntriesOfType(BccTreeTextType).Where(rdb.CanRead))
			{
				var body = rdb.ReadDecompressed(entry);
				if (body.Length == 0) // CDN miss
					continue;

				// ⚠️ Latin1, not UTF8: the body opens with a short binary header before the `<?xml` and a UTF8 decode
				// would substitute over those bytes. Nothing here reads them, but corrupting them silently is how a
				// header that later turns out to matter goes unnoticed.
				var text = Encoding.Latin1.GetString(body);
				var name = BctMeshName.Match(text);
				if (!name.Success)
					continue;

				trees++;
				foreach (Match m in BctNode.Matches(text))
				{
					rows.Add(new BctNodeRow(entry.Id, name.Groups[1].Value, m.Groups[1].Value, int.Parse(m.Groups[2].Value),
						m.Groups[3].Value, m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : null));
				}
			}

			var substates = rows.Where(r => r.Kind == "substate").ToList();
			var states = rows.Where(r => r.Kind == "state").ToList();

			Console.WriteLine($"BCC trees (RDB {BccTreeTextType}, text XML): {trees} tree(s), "
			                  + $"{substates.Count} substate declaration(s), {states.Count} state declaration(s)");

			// ⭐ The collision census, printed for BOTH kinds because the contrast is the finding: it is what says a
			// substate id->name map is publishable and a state one is not.
			foreach (var kind in new[] { "substate", "state" })
			{
				var of = kind == "substate" ? substates : states;
				var byId = of.GroupBy(r => r.Id).ToList();
				var clashes = byId.Where(g => g.Select(r => r.Name).Distinct().Count() > 1).ToList();
				var priorityClashes = byId.Where(g => g.Select(r => r.Priority).Distinct().Count() > 1).ToList();

				Console.WriteLine($"  {kind,-9} {byId.Count,3} distinct id(s)   name collisions across trees: "
				                  + (clashes.Count == 0
					                  ? "NONE"
					                  : string.Join(", ", clashes.Select(g => $"{g.Key}={string.Join('/', g.Select(r => r.Name).Distinct())}")))
				                  + $"   priority disagreements: {(priorityClashes.Count == 0 ? "NONE" : priorityClashes.Count.ToString())}");
			}

			// ⭐ STATE id -> name, per tree, for the ids the caller asks about (#905). Stat 0x14 is the client's own
			// "BCC State", so a stat-20 value IS a state id from this table - which is what makes the four unnamed
			// values the corpus carries (69, 41, 9, 72) answerable at all. It is a LOOKUP rather than a dump because
			// state ids genuinely collide across trees (11 is `LoopingEmote` in one and `SneakHiding` in another), so
			// the only honest answer names the tree alongside the name.
			if (!string.IsNullOrWhiteSpace(lookup))
			{
				var wanted = lookup.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(t => int.TryParse(t, out var v) ? v : -1)
					.Where(v => v >= 0)
					.ToHashSet();

				Console.WriteLine();
				Console.WriteLine($"STATE id lookup for {string.Join(", ", wanted.Order())} - the space `CharacterStatId.CombatMode` (stat 0x14, the client's \"BCC State\") draws from:");
				foreach (var id in wanted.Order())
				{
					var hits = states.Where(r => r.Id == id).ToList();
					if (hits.Count == 0)
					{
						Console.WriteLine($"  {id,4} -- declared by NO shipped tree");
						continue;
					}

					foreach (var g in hits.GroupBy(r => r.Name).OrderByDescending(g => g.Count()))
						Console.WriteLine($"  {id,4} {g.Key,-26} in {g.Count()} tree(s): {string.Join(", ", g.Select(r => r.Tree).Distinct().Order())}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("SUBSTATE id -> name. This is what a BehaviorControlCenter phase 0x0B (enter) / 0x0C (leave)");
			Console.WriteLine("record carries as its bare u32 (#810).");
			Console.WriteLine($"  {"id",4} {"name",-24} {"prio",4}  declared in");
			foreach (var g in substates.GroupBy(r => r.Id).OrderBy(g => g.Key))
			{
				var first = g.First();
				Console.WriteLine($"  {g.Key,4} {first.Name,-24} {first.Priority?.ToString() ?? "-",4}  "
				                  + string.Join(", ", g.Select(r => r.Tree).Distinct().OrderBy(t => t)));
			}

			Console.WriteLine();
			Console.WriteLine($"  {"tree",-22} {"rdb id",9}  substates  states");
			foreach (var g in rows.GroupBy(r => (r.TreeId, r.Tree)).OrderBy(g => g.Key.Tree).ThenBy(g => g.Key.TreeId))
			{
				Console.WriteLine($"  {g.Key.Tree,-22} {g.Key.TreeId,9}  {g.Count(r => r.Kind == "substate"),9}  "
				                  + $"{g.Count(r => r.Kind == "state"),6}");
			}
		}
	}
}
