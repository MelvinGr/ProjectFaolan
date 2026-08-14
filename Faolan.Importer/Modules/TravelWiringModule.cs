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
	// of a trip on the wire, so there is nothing left to author: the branch comes from the player's KnubotAnswer pick
	// (the screen it answered, and that option's own LDB id), the destination and arrival from the trip's own
	// GameActionListIIR ("xpos=..;ypos=..;zpos=..;PlayFieldID=..;Local=1", TravelActionCodec), and the fare from the
	// wallet delta across the conversation. See TravelDestinationExtractor.
	//
	// A separate module rather than part of `conversations` because it is a different harvest over the same packets and
	// writes a different table.
	public sealed class TravelWiringModule : IImportModule
	{
		// Maps must exist first (DestinationMapId is an FK) and so must the CONVERSATIONS: since #642 the row is keyed
		// by the dialogue screen its branch leaves, so the screen has to be there to point at. That is stricter than
		// before - the old (name, answer path) key stood on its own - and it is the point: a trip whose branch does not
		// exist is dead wiring, and now it cannot be written at all rather than sitting there unreachable.
		public IReadOnlyList<string> DependsOn => [nameof(MapsModule), nameof(ConversationsModule)];

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
			// The screen that presents each NPC's destination list, by its content - the join onto the imported graph.
			// A content key that matches several screens of one NPC is a discriminated pair (ConversationNode.
			// Discriminator); the lowest one is taken and the ambiguity reported rather than guessed at silently.
			var screens = (await ctx.Db.ConversationNodes.Select(n => new
				{
					n.Id,
					n.NpcName,
					n.ContentKey,
					n.Discriminator
				})
				.ToListAsync())
				.GroupBy(n => (n.NpcName, n.ContentKey))
				.ToDictionary(g => g.Key, g => g.OrderBy(n => n.Discriminator).First().Id);

			var added = 0;
			var updated = 0;
			var skipped = 0;

			foreach (var trip in trips)
			{
				// A missing map would fail the FK at SaveChanges with nothing naming the row, so say which one.
				if (!known.Contains(trip.DestinationMapId))
				{
					Console.WriteLine($"  travel-wiring: skipping \"{trip.NpcName}\" option {trip.OptionId} - map {trip.DestinationMapId} is not imported.");
					skipped++;
					continue;
				}

				if (!screens.TryGetValue((trip.NpcName, trip.FromContentKey), out var fromNodeId))
				{
					Console.WriteLine($"  travel-wiring: skipping \"{trip.NpcName}\" option {trip.OptionId} - the screen that offered it is not in the imported graph.");
					skipped++;
					continue;
				}

				var row = await ctx.Db.TravelDestinations.FirstOrDefaultAsync(d => d.FromNodeId == fromNodeId && d.OptionId == trip.OptionId);
				if (row == null)
				{
					ctx.Db.TravelDestinations.Add(new TravelDestination
					{
						FromNodeId = fromNodeId,
						OptionId = trip.OptionId,
						NpcName = trip.NpcName,
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

				// ⚠️ A row sourced from a `{teleportplayer}` DIRECTIVE is not refreshed from the wire (#821). The two
				// state different things about the same trip: the directive is the decoded grammar
				// (`{teleportplayer <x> <z> [y <y>] <map>}`, where a MISSING height means ground-snap at run time and
				// is stored as a NULL Y), while an n3Teleport states one session's actual arrival. Overwriting the
				// first with the second silently replaces a [V2]-cited decode with a sample - it turned Sigurd's
				// (55.7, 158.1, 113.9) into (55, 158, 113) and gave Wagoner Polus a height his directive deliberately
				// omits. The directive is the better source for the rows it owns, so it wins.
				if (row.FromDialogueDirective)
					continue;

				row.NpcName = trip.NpcName;
				row.DestinationMapId = trip.DestinationMapId;
				row.Cost = trip.Cost;
				row.ArrivalX = trip.ArrivalX;
				row.ArrivalY = trip.ArrivalY;
				row.ArrivalZ = trip.ArrivalZ;
				updated++;
			}

			await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{trips.Count} trip(s) harvested: {added} added, {updated} refreshed" +
			                        (cleared > 0 ? $", {cleared} cleared" : "") + (skipped > 0 ? $", {skipped} skipped (map or screen not imported)" : "") + ".");
		}
	}
}
