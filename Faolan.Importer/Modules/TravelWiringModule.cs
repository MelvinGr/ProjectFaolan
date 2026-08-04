using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// LOCAL TRAVEL wiring (#506/#514): which dialogue branch of which NPC sells which trip, for how much, and where it
	// drops the player.
	//
	// ⚠️ HARVESTED, not hand-authored - unlike `quest-wiring`, which it otherwise resembles. Retail states every field
	// of a trip on the wire, so there is nothing left to author: the answer path comes from the player's KnubotAnswer
	// picks, the destination and arrival from the trip's own GameActionListIIR ("xpos=..;ypos=..;zpos=..;
	// PlayFieldID=..;Local=1", TravelActionCodec), and the fare from the wallet delta across the conversation. See
	// TravelDestinationExtractor.
	//
	// A separate module rather than part of `conversations` because it is a different harvest over the same packets and
	// writes a different table - and because a targeted re-run of one must not drag the other's heavy rebuild in.
	public sealed class TravelWiringModule : IImportModule
	{
		// Maps must exist first - DestinationMapId is an FK. Conversations are NOT a hard dependency: the row is keyed
		// by (name, answer path) and stands on its own, so a targeted `--only travel-wiring` run must not drag the
		// whole dialogue re-harvest in. The runtime simply finds no node to fire it on until they are imported.
		public IReadOnlyList<string> DependsOn => [nameof(MapsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Local-travel wiring: the dialogue branch that sells each trip, its fare and arrival (#514).";
		public string Name => "travel-wiring";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var trips = TravelDestinationExtractor.Merge(ctx.Captures.SelectMany(c => TravelDestinationExtractor.Extract(c.Packets)));

			var cleared = 0;
			if (ctx.ModeFor(Name) == WriteMode.Overwrite)
			{
				var existing = await ctx.Db.TravelDestinations.ToListAsync();
				ctx.Db.TravelDestinations.RemoveRange(existing);
				cleared = existing.Count;
				await ctx.Db.SaveChangesAsync();
			}

			var known = (await ctx.Db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var added = 0;
			var updated = 0;
			var skipped = 0;

			foreach (var trip in trips)
			{
				// A missing map would fail the FK at SaveChanges with nothing naming the row, so say which one.
				if (!known.Contains(trip.DestinationMapId))
				{
					Console.WriteLine($"  travel-wiring: skipping \"{trip.NpcName}\" '{trip.AnswerPath}' - map {trip.DestinationMapId} is not imported.");
					skipped++;
					continue;
				}

				var row = await ctx.Db.TravelDestinations.FirstOrDefaultAsync(d => d.NpcName == trip.NpcName && d.AnswerPath == trip.AnswerPath);
				if (row == null)
				{
					ctx.Db.TravelDestinations.Add(new TravelDestination
					{
						NpcName = trip.NpcName,
						AnswerPath = trip.AnswerPath,
						DestinationMapId = trip.DestinationMapId,
						Cost = trip.Cost,
						ArrivalX = trip.ArrivalX,
						ArrivalY = trip.ArrivalY,
						ArrivalZ = trip.ArrivalZ
					});
					added++;
					continue;
				}

				if (ctx.ModeFor(Name) == WriteMode.Insert)
					continue;

				row.DestinationMapId = trip.DestinationMapId;
				row.Cost = trip.Cost;
				row.ArrivalX = trip.ArrivalX;
				row.ArrivalY = trip.ArrivalY;
				row.ArrivalZ = trip.ArrivalZ;
				updated++;
			}

			await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{trips.Count} trip(s) harvested: {added} added, {updated} refreshed" +
			                        (cleared > 0 ? $", {cleared} cleared" : "") + (skipped > 0 ? $", {skipped} skipped (map not imported)" : "") + ".");
		}
	}
}
