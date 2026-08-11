using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;

namespace Faolan.Importer.Cli
{
	// `--report class-hierarchy`: the client's own archetype -> class membership (RDB 1000506), checked against the
	// hard-coded `ConanArchetypes` (Gitea #469).
	//
	// ⭐ `ConanArchetypes` states, in its own comment, that "the client ships the archetype IDS (1000505 tier 1) but
	// no MEMBERSHIP table", and therefore takes the twelve placements from the game's published documentation.
	// **RDB 1000506 is that table.** Its loader names it outright — `FUN_0078de40` bails with *"Could not load
	// classmembership info. This will cause gui to loose parent class feats"* — and all twelve placements agree
	// exactly, so the published-documentation citation becomes a corroboration rather than the source.
	//
	// The report exists because the constant STAYS hard-coded: the callers that need it run in capture-driven
	// importer modules with no `RdbDir`, which is the reason it was inlined in the first place. So the client's table
	// cannot be the runtime source, and the honest alternative is a tool that proves the two agree - the same shape
	// as `--report creature-races`' cross-check.
	//
	// ⚠️ TWO entries ship and they are not versions of each other. Entry 0 is the full AUTHORED hierarchy - 27 edges,
	// including classes this build never shipped - and entry 1 is the 16 that did. Checking against entry 0 would
	// report ten phantom "missing" classes, so the check reads entry 1 and entry 0 is printed beside it as design
	// history.
	public static partial class ReportCli
	{
		private static void ClassHierarchy(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);

			var entries = new List<(uint Id, ClassMembership Table)>();
			foreach (var id in (uint[])[0, ClassMembershipTable.ShippedId])
			{
				if (rdb.TryGet(ClassMembershipTable.RdbType, id, out var entry) && rdb.CanRead(entry))
					entries.Add((id, ClassMembershipTable.Parse(rdb.ReadDecompressed(entry))));
			}

			if (entries.Count == 0)
			{
				Console.WriteLine($"RDB {ClassMembershipTable.RdbType} is not readable in this install.");
				return;
			}

			// The class NAMES come from 1000505, the registry the ids belong to - printed rather than assumed, so a
			// row naming a class we have no name for is visible.
			var names = ClassRegistryNames(rdb);

			foreach (var (id, table) in entries)
			{
				var kind = id == ClassMembershipTable.ShippedId ? "the SHIPPED set" : "the full AUTHORED hierarchy (includes classes this build never shipped)";
				Console.WriteLine($"RDB {ClassMembershipTable.RdbType}/{id} - {table.Edges.Count} edge(s), declared {table.DeclaredCount}, "
				                  + $"{table.TrailingBytes} trailing byte(s): {kind}");

				foreach (var parent in table.Parents)
				{
					var children = table.ChildrenOf(parent).Select(c => $"{c} {names.GetValueOrDefault(c, "?")}");
					Console.WriteLine($"  {parent,3} {Trim(names.GetValueOrDefault(parent, "?"), 12),-12} -> {string.Join(", ", children)}");
				}

				Console.WriteLine();
			}

			var shipped = entries.FirstOrDefault(e => e.Id == ClassMembershipTable.ShippedId).Table;
			if (shipped == null)
			{
				Console.WriteLine($"⚠️ entry {ClassMembershipTable.ShippedId} is absent, so `ConanArchetypes` cannot be checked.");
				return;
			}

			Console.WriteLine("⭐ THE CHECK - `ConanArchetypes` (published documentation + one corpus corroboration) against the");
			Console.WriteLine("   client's own table. Every disagreement is a bug in the constant:");
			Console.WriteLine();

			var problems = 0;
			foreach (var archetype in Enum.GetValues<ConanArchetype>())
			{
				// Commoner is the pre-class starting state: it parents the four ARCHETYPES, not classes, so it has no
				// `ConanArchetypes` row and is reported below as the root rather than compared here.
				if (archetype == ConanArchetype.Commoner)
					continue;

				var ours = ConanArchetypes.ClassesOf(archetype).Select(c => (uint)c).Order().ToList();
				var theirs = shipped.ChildrenOf((uint)archetype).Order().ToList();

				var agree = ours.SequenceEqual(theirs);
				if (!agree)
					problems++;

				Console.WriteLine($"  {archetype,-9} ours [{string.Join(",", ours)}]  client [{string.Join(",", theirs)}]  {(agree ? "agree" : "⛔ DISAGREE")}");
			}

			// Commoner parents the four ARCHETYPES rather than any class, which is why it has no `ConanArchetypes`
			// row - stated so the empty line above is not read as a gap.
			Console.WriteLine($"  {"(root)",-9} Commoner (0) parents the archetypes themselves: [{string.Join(",", shipped.ChildrenOf(0).Order())}]");
			Console.WriteLine();
			Console.WriteLine(problems == 0
				? "  ⭐ Every placement agrees. The published-documentation citation in ConanArchetypes is now corroboration,\n"
				  + "     not the source - the client states the membership itself, in RDB 1000506."
				: $"  ⛔ {problems} archetype(s) disagree with the client. ConanArchetypes is wrong; the client is authoritative.");
		}

		// 1000505 `RDBPlayerClassData_c`: `[u32 classId][u32 nameLength][name][u32 tier]`, variable-width.
		private static Dictionary<uint, string> ClassRegistryNames(RdbReader rdb)
		{
			var names = new Dictionary<uint, string>();
			if (!rdb.TryGet(1000505, 0, out var entry) || !rdb.CanRead(entry))
				return names;

			var body = rdb.ReadDecompressed(entry);
			var offset = 16;
			while (offset + 8 <= body.Length)
			{
				var classId = Core.Extensions.SpanExtensions.ReadU32Le(body, offset);
				var length = (int)Core.Extensions.SpanExtensions.ReadU32Le(body, offset + 4);
				if (length is <= 0 or > 64 || offset + 8 + length + 4 > body.Length)
					break;

				names[classId] = System.Text.Encoding.Latin1.GetString(body, offset + 8, length);
				offset += 8 + length + 4; // the trailing u32 is the TIER (1 = archetype, 3 = class)
			}

			return names;
		}
	}
}
