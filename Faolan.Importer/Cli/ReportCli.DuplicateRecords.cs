using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report duplicate-records`: which s2c StatIIR records does retail send twice, back to back and byte-identical?
	//
	// The case that raises it is the hostile-flip `CarsGroup` restate, which arrives as two separate packets sharing
	// one TCP segment, with no second value and no `0x00`/`0x01` env pair, in every capture that carries it. Both are
	// replayed because the capture says so; what is unknown is why there is a second copy at all, a stat set being
	// idempotent.
	//
	// So the question is whether duplication is a property of that record or a thing retail does generally. If many
	// stats arrive doubled then two emitters on one event is an ordinary shape and CarsGroup is not a puzzle; if it is
	// alone in it, the doubling is specific and worth explaining.
	//
	// Adjacency is required, not just recurrence: a stat that is simply sent often would dominate any count of
	// repeats, so a pair counts only when the second copy is the very next StatIIR for that entity with nothing
	// between them, which is what the observation actually is.
	//
	// Open work tracked in Gitea #842.
	public static partial class ReportCli
	{
		// The `CarsGroup` value a dialogue-driven hostile flip sets (`NpcStatCore.MobCarsGroup`).
		private const uint HostileFlipValue = 3;

		private static int DuplicateRecords(IReadOnlyList<NamedCapture> captures)
		{
			var pairs = new Dictionary<uint, int>();
			var singles = new Dictionary<uint, int>();
			var carsGroupCaptures = new List<string>();

			// The subject is the hostile flip specifically - `CarsGroup == 3` on a character - so that value is split
			// out rather than pooled with every other CarsGroup send, or the comparison is not like for like.
			var flipDoubled = 0;
			var flipSingle = 0;
			var arrivals = 0;
			var parsed = 0;

			foreach (var capture in captures)
			{
				// The previous StatIIR seen, per entity: a duplicate is the same (entity, stat, value) arriving with
				// nothing else for that entity in between.
				var previous = new Dictionary<uint, (uint StatId, uint Value)>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsDownload: true, IsIirCarrier: true, SubOpcode: StatCodec.Opcode })
						continue;

					arrivals++;
					if (!p.TryParse(StatCodec.Parse, out var stat))
						continue;

					parsed++;
					var repeated = previous.TryGetValue(stat.Handle, out var last) && last.StatId == stat.StatId && last.Value == stat.Value;
					if (repeated)
					{
						pairs[stat.StatId] = pairs.GetValueOrDefault(stat.StatId) + 1;
						if (stat.StatId == (uint)CharacterStatId.CarsGroup && !carsGroupCaptures.Contains(capture.Name))
							carsGroupCaptures.Add(capture.Name);
					}
					else
					{
						singles[stat.StatId] = singles.GetValueOrDefault(stat.StatId) + 1;
					}

					if (stat.StatId == (uint)CharacterStatId.CarsGroup && stat.Value == HostileFlipValue)
					{
						if (repeated)
							flipDoubled++;
						else
							flipSingle++;
					}

					previous[stat.Handle] = (stat.StatId, stat.Value);
				}
			}

			Console.WriteLine("=== which StatIIR records does retail send TWICE, back to back? (#842) ===");
			Console.WriteLine();
			// Counted at arrival, ahead of the parse, so "no packet carried this" cannot read as "none was doubled".
			Console.WriteLine($"  control - StatIIR: {arrivals,7} arrived, {parsed,7} parsed");
			Console.WriteLine();

			var stats = pairs.Keys.Union(singles.Keys).OrderByDescending(id => pairs.GetValueOrDefault(id)).ToList();
			Console.WriteLine($"  {"stat",-34}  {"doubled",8}  {"single",8}   share");
			foreach (var id in stats.Take(20))
			{
				var doubled = pairs.GetValueOrDefault(id);
				var single = singles.GetValueOrDefault(id);
				var name = Enum.IsDefined((CharacterStatId)id) ? $"{(CharacterStatId)id} (0x{id:X})" : $"0x{id:X}";
				Console.WriteLine($"  {name,-34}  {doubled,8}  {single,8}   {(doubled + single == 0 ? 0 : doubled * 100.0 / (doubled + single)),4:F0}%");
			}

			Console.WriteLine();
			var carsDoubled = pairs.GetValueOrDefault((uint)CharacterStatId.CarsGroup);
			var carsSingle = singles.GetValueOrDefault((uint)CharacterStatId.CarsGroup);
			Console.WriteLine($"  CarsGroup specifically: {carsDoubled} doubled, {carsSingle} single, across {carsGroupCaptures.Count} capture(s).");
			foreach (var name in carsGroupCaptures)
				Console.WriteLine($"    {name}");

			Console.WriteLine($"  the hostile flip itself (CarsGroup == {HostileFlipValue}): {flipDoubled} doubled, {flipSingle} single.");

			// The finding. A stat whose doubled count equals its single count is one that always arrives as a pair -
			// each pair contributing one "first" and one "repeat" - which is a property of retail's sender, not of the
			// event. That is the shape the hostile flip has, and it is not rare.
			var alwaysPaired = pairs.Where(kv => singles.GetValueOrDefault(kv.Key) == kv.Value && kv.Value > 0)
				.OrderByDescending(kv => kv.Value)
				.ToList();

			Console.WriteLine();
			var others = pairs.Where(kv => kv.Key != (uint)CharacterStatId.CarsGroup).Sum(kv => kv.Value);
			if (others == 0)
			{
				Console.WriteLine("  ⭐ CarsGroup is the ONLY stat retail ever sends doubled, so the second copy is specific to it and");
				Console.WriteLine("     #842's question is a real one rather than an instance of a general habit.");
				return 0;
			}

			Console.WriteLine($"  ⭐ Doubling is NOT specific to the hostile flip: {others} back-to-back duplicates across");
			Console.WriteLine($"     {pairs.Count - 1} other stat id(s). So a repeated set is an ordinary shape of retail's sender.");

			if (alwaysPaired.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("     ...and these are sent as a pair EVERY time - doubled count equals single count, i.e. one 'first'");
				Console.WriteLine("     and one 'repeat' per event, which is a property of the sender rather than of any one trigger:");
				foreach (var kv in alwaysPaired.Take(10))
				{
					var name = Enum.IsDefined((CharacterStatId)kv.Key) ? $"{(CharacterStatId)kv.Key}" : $"0x{kv.Key:X}";
					Console.WriteLine($"       {name,-30} {kv.Value} pair(s)");
				}
			}

			return 0;
		}
	}
}
