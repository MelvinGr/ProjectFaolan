using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report travel-trips`: every local TRAVEL trip the captures yield, with the capture that yielded it.
	//
	// The wiring module prints a count, and a count cannot answer the question that matters when the harvest changes:
	// which trips, and are the ones that disappeared the bogus ones? That question came up the moment a guard was
	// added for teleports a door had already claimed (The Architect, who was being credited with the Black Castle exit
	// door's changemap), and a count alone could not tell a fixed attribution from a lost trip.
	public static partial class ReportCli
	{
		private static void TravelTrips(IReadOnlyList<NamedCapture> captures)
		{
			var rows = new List<(string Capture, TravelDestinationExtractor.Entry Trip)>();
			foreach (var capture in captures)
				foreach (var trip in TravelDestinationExtractor.Extract(capture.Packets))
					rows.Add((capture.Name, trip));

			Console.WriteLine($"travel-trips: {rows.Count} trip(s) harvested from {captures.Count} capture(s), "
			                  + $"{rows.Select(r => r.Trip.NpcName).Distinct().Count()} distinct NPC(s).");
			Console.WriteLine();
			Console.WriteLine("npc                        map  cost   arrival                        capture");

			foreach (var (capture, t) in rows.OrderBy(r => r.Trip.NpcName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Trip.DestinationMapId))
				Console.WriteLine($"{t.NpcName,-26} {t.DestinationMapId,4}  {(t.Cost?.ToString() ?? "-"),5}  "
				                  + $"({t.ArrivalX,8:0.0},{t.ArrivalY,7:0.0},{t.ArrivalZ,8:0.0})  {capture[..Math.Min(46, capture.Length)]}");
		}
	}
}
