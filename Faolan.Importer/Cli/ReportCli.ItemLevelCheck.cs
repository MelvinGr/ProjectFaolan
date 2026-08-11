using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report item-level-check`: does every shipped `Items.Level` match the level retail puts on the wire?
	// (Gitea #729, moved out of `ItemLevelTests`; the defect it exists for is #725.)
	//
	// The bug: the shipped levels predated the visible git history and 12 measurable templates disagreed with every
	// captured source - always by exactly a factor of ten (Royal Signet 72 -> 7, Necklace of Tireless Sagacity
	// 80 -> 8, Small Chipped Fang 10 -> 1). Nothing corrected them, because the only writer set `Level` on INSERT and
	// skipped any template already in `Items` - the same first-record-wins shape that cost us the item hash in #719.
	//
	// ⭐ **Why it is a REPORT and not a test**, and it is the same argument as `--report npc-pose-check`: it compares
	// importer OUTPUT (`faolan.db`) against importer INPUT (the dumps). As a test it read the input through
	// `fixtures.bin`'s corpus half - a frozen, derived slice - so the two populations diverge the moment captures are
	// added, and the gate then judges the shipped value against evidence it has already outgrown. The sanctioned fix
	// for that (regenerate the extract) rebaselines twenty-odd unrelated gates in one unreviewable commit, which is
	// why the pressure is always to weaken the gate instead. Run against the real dumps there is no second population
	// and no such failure mode.
	//
	// ⚠️ What stays a TEST is the part that is a property of the CODE rather than of the data: the snapshot reader
	// itself is gated by the corpus round-trips (#384/#390/#391). This report asserts nothing about parsing.
	public static partial class ReportCli
	{
		// How much of the corpus-visible population must actually carry a shipped level before the equality check
		// above it means anything. A regression that stopped writing `Level` would leave the column at its old 9%
		// coverage while every equality still passed - vacuously.
		private const double ItemLevelCoverageFloor = 0.9;

		private static async Task<int> ItemLevelCheck(IReadOnlyList<NamedCapture> captures)
		{
			// template -> the level every captured snapshot entry reports for it, and the templates that report more
			// than one (which would mean `Items.Level` cannot represent it at all).
			var wire = new Dictionary<uint, ulong>();
			var varying = new HashSet<uint>();
			var snapshots = 0;

			foreach (var capture in captures)
			{
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: FullPlayerCharacterCodec.Opcode })
						continue;

					ProtoFullCharacterIIR root;
					try
					{
						root = SnapshotRoot.Of(p.Body.AsSpan(4));
					}
					catch
					{
						continue; // not a decodable snapshot root - the round-trip gates are what police that
					}

					snapshots++;
					foreach (var container in root.Inventories)
					foreach (var entry in SnapshotRoot.EntriesOf(container))
					{
						if (SnapshotRoot.TemplateIdOf(entry) is not { } template || template == 0)
							continue;
						if (SnapshotRoot.LevelOf(entry) is not { } level)
							continue;

						if (wire.TryGetValue(template, out var seen) && seen != level)
							varying.Add(template);
						else
							wire[template] = level;
					}
				}
			}

			await using var db = OpenDb();
			var shipped = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Level);

			Console.WriteLine($"item-level-check over {captures.Count} capture(s): {snapshots} snapshot root(s), "
				+ $"{wire.Count} template(s) carry a wire level.");

			var wrong = wire
				.Where(w => shipped.TryGetValue(w.Key, out var lvl) && lvl != 0 && lvl != w.Value)
				.Select(w => $"item {w.Key}: shipped {shipped[w.Key]}, wire {w.Value}"
					+ (shipped[w.Key] == w.Value / 10 ? "   <- exactly a TENTH, the #725 defect" : ""))
				.ToList();

			var covered = wire.Count(w => shipped.TryGetValue(w.Key, out var lvl) && lvl != 0);
			Console.WriteLine($"  {covered}/{wire.Count} of them have a shipped Items.Level "
				+ $"({(wire.Count == 0 ? 0 : 100.0 * covered / wire.Count):0.0}%).");

			var failed = false;

			// The premise the whole column rests on: the level is a per-TEMPLATE property, so one column can hold it.
			if (varying.Count > 0)
			{
				failed = true;
				Console.WriteLine($"  ⚠️ {varying.Count} template(s) carry MORE THAN ONE wire level, so Items.Level cannot represent them:");
				foreach (var t in varying.Take(10))
					Console.WriteLine($"    template {t}");
			}

			if (wrong.Count > 0)
			{
				failed = true;
				Console.WriteLine($"  ⚠️ {wrong.Count} shipped item level(s) disagree with the captured wire:");
				foreach (var w in wrong.Take(20))
					Console.WriteLine($"    {w}");
			}

			if (wire.Count > 0 && covered < wire.Count * ItemLevelCoverageFloor)
			{
				failed = true;
				Console.WriteLine($"  ⚠️ only {covered} of {wire.Count} templates seen in the corpus have a shipped Items.Level - "
					+ "the level harvest has regressed (it reached 3,635 items when #725 landed).");
			}

			if (!failed)
				Console.WriteLine("  ✅ every shipped level matches the wire, and the harvest's coverage holds.");

			return failed ? 1 : 0;
		}
	}
}
