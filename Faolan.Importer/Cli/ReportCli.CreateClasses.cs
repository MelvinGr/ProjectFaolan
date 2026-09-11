using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report create-classes`: which class each create capture selected, and how many spell/combo grants arrived
	// under each.
	//
	// Attributing a whole capture to one class - the first `ChangeClass` callback in it - is only correct when the
	// player picked one class and stopped. Retail sends the clicked class's spell and combo book each time so the
	// editor can display it, so a capture where the player browsed carries several books, and a per-capture
	// attribution merges them onto whichever class came first. This report makes that visible rather than arguable: a
	// row with more than one class is a capture no per-capture rule can get right.
	public static partial class ReportCli
	{
		private static void CreateClasses(IReadOnlyList<NamedCapture> captures)
		{
			var creates = captures.Where(c => c.Name.Contains("creat", StringComparison.OrdinalIgnoreCase)).ToList();
			Console.WriteLine($"{creates.Count} create-flow capture(s) of {captures.Count}.");
			Console.WriteLine();

			var browsed = 0;
			var totals = new Dictionary<uint, (int Spells, int Combos)>();

			foreach (var capture in creates.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				var packets = (IReadOnlyList<byte[]>)capture.Packets.Select(p => p.Bytes).ToList();
				var selected = CaptureAbilityImporter.SelectedClasses(packets);
				var perClass = new Dictionary<uint, (int Spells, int Combos)>();

				foreach (var (cls, packet) in CaptureAbilityImporter.PacketsWithSelectedClass(packets))
				{
					if (cls is not { } c)
						continue;

					var spells = CaptureSpellImporter.CountLearnGrants(packet);
					var combos = CaptureAbilityImporter.CountComboGrants(packet);
					if (spells == 0 && combos == 0)
						continue;

					var row = perClass.GetValueOrDefault(c);
					perClass[c] = (row.Spells + spells, row.Combos + combos);

					var total = totals.GetValueOrDefault(c);
					totals[c] = (total.Spells + spells, total.Combos + combos);
				}

				if (selected.Count > 1)
					browsed++;

				var names = string.Join(" -> ", selected.Select(Name));
				var grants = string.Join("  ", perClass.OrderBy(x => x.Key).Select(x => $"{Name(x.Key)}: {x.Value.Spells} spell / {x.Value.Combos} combo"));
				Console.WriteLine($"{capture.Name[..Math.Min(58, capture.Name.Length)],-60} selected={{{names}}}");
				if (grants.Length > 0)
					Console.WriteLine($"{"",-60} {grants}");
			}

			Console.WriteLine();
			Console.WriteLine($"{browsed} of {creates.Count} create captures selected MORE THAN ONE class - each of those was attributed entirely to its first.");
			Console.WriteLine();
			Console.WriteLine("grants by the class selected AT THE TIME:");
			foreach (var (cls, row) in totals.OrderByDescending(x => x.Value.Spells + x.Value.Combos))
				Console.WriteLine($"  {Name(cls),-20} {row.Spells,5} spell grants  {row.Combos,5} combo grants");
			return;

			static string Name(uint cls)
			{
				return Enum.IsDefined((ConanClass)cls) ? ((ConanClass)cls).ToString() : $"class {cls}";
			}
		}
	}
}
