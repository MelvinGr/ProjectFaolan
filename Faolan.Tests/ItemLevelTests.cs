using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #725: `Items.Level` must be the level retail puts on the wire.
	//
	// It was not. The shipped values predated the visible git history and 12 measurable templates disagreed with
	// every captured source - always by exactly a factor of ten (Royal Signet 72 -> 7, Necklace of Tireless
	// Sagacity 80 -> 8, Small Chipped Fang 10 -> 1). Nothing corrected them, because the only writer
	// (`CaptureListingImporter.HarvestItems`) sets `Level` on INSERT and skips any template already in `Items` -
	// the same first-record-wins shape that cost us the item hash in #719. The level is now harvested by
	// `ItemHashHarvester` from every source that carries one, and the importer UPDATES existing rows.
	//
	// This reads the corpus half of the fixture archive, because the assertion IS breadth: one snapshot would go
	// vacuous, and the defect was only visible across many templates.
	public class ItemLevelTests
	{
		private readonly ITestOutputHelper _o;

		public ItemLevelTests(ITestOutputHelper o)
		{
			_o = o;
		}

		[Fact]
		public async Task EveryShippedLevel_MatchesTheCapturedWireLevel()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var shipped = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Level, TestContext.Current.CancellationToken);

			// template -> the level every captured snapshot entry reports for it
			var wire = new Dictionary<uint, ulong>();
			var varying = new HashSet<uint>();
			foreach (var entry in CorpusExtract.PacketsOfType((uint)IirTypes.FullPlayerCharacterIIR))
			{
				ProtoFullCharacterIIRRoot(entry, wire, varying);
			}

			_o.WriteLine($"{wire.Count} templates carry a level across the corpus snapshots");

			// The premise the fix rests on: the level is a per-TEMPLATE property, so one column can hold it.
			Assert.True(varying.Count == 0,
				"a template carries more than one wire level, so Items.Level cannot represent it:\n  "
				+ string.Join("\n  ", varying.Take(10)));

			var wrong = wire
				.Where(w => shipped.TryGetValue(w.Key, out var lvl) && lvl != 0 && lvl != w.Value)
				.Select(w => $"item {w.Key}: shipped {shipped[w.Key]}, wire {w.Value}"
				             + (shipped[w.Key] == w.Value / 10 ? "   <- exactly a TENTH, the #725 defect" : ""))
				.ToList();

			Assert.True(wrong.Count == 0, $"{wrong.Count} shipped item level(s) disagree with the captured wire:\n  " + string.Join("\n  ", wrong.Take(20)));

			// And the harvest must actually have run: a regression that stopped writing Level would leave the
			// column at its old 9% coverage while the equality check above still passed vacuously.
			var covered = wire.Count(w => shipped.TryGetValue(w.Key, out var lvl) && lvl != 0);
			_o.WriteLine($"{covered}/{wire.Count} of them have a shipped level");
			Assert.True(covered >= wire.Count * 0.9,
				$"only {covered} of {wire.Count} templates seen in the corpus have a shipped Items.Level - the level "
				+ "harvest has regressed (it reached 3,635 items when #725 landed).");
		}

		private static void ProtoFullCharacterIIRRoot(CorpusExtract.Entry entry, Dictionary<uint, ulong> wire, HashSet<uint> varying)
		{
			var root = SnapshotRoot.Of(entry.Body.AsSpan(4));
			foreach (var container in root.Inventories)
			{
				foreach (var e in SnapshotRoot.EntriesOf(container))
				{
					var template = SnapshotRoot.TemplateIdOf(e);
					var level = SnapshotRoot.LevelOf(e);
					if (template is not { } t || t == 0 || level is not { } l)
						continue;

					if (wire.TryGetValue(t, out var seen) && seen != l)
						varying.Add(t);
					else
						wire[t] = l;
				}
			}
		}
	}
}
