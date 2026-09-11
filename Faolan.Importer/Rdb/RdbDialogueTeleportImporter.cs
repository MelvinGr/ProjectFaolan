using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Harvests the second local-travel mechanism: the travellers who carry their destination inline in their
	// dialogue text, as a `{teleportplayer}` directive the server acts on, instead of offering a paid destination list.
	//
	// the DIRECTIVE is not on the wire and never REACHES the server at RUNTIME. A conversation screen carries only
	// an LDB reference (category + string id + the per-string key); the text itself lives in the client's install, and
	// the server ships no LDB. So the only way to act on a directive is to read it here, at import, and store what it
	// says - which is why this is an RDB module and not a dialogue-VM feature.
	//
	// The three travellers the client ships, all found by sweeping every conversation screen we hold:
	//
	//   Tortage Smuggler  "Then we go.{delay 6}{teleportplayer 210 205 y 11 4579}"
	//   Wagoner Polus     "Climb aboard, then. […]{delay 3}{teleportplayer 349 564 1000}{hidegoodbye}{quit}"
	//   Sigurd            "Then I wish you luck.{gameactiononnpc …}{teleportplayer 55.7 113.9 y 158.1 4020}{showgoodbye}"
	//
	// Sigurd is the same mechanism with no `{delay}` (retail teleports as the line lands) and an extra
	// `{gameactiononnpc StartClientSideScryScript}` that plays a cutscene - unwired, and independent of the trip.
	//
	// Grammar, from the three of them plus the capture that confirms one:
	//
	//     {teleportplayer <x> <z> [y <y>] <mapId>}
	//
	// The last token is the destination playfield and the leading ones are the arrival position, X then Z, with the
	// height carried optionally behind a literal `y`. [V2: NecroL80_..._disconnect-after-wayfarer t=105.8 -> 111.7 ->
	// 112.3 - the Smuggler's line, then IntendedTeleport naming 4579, and the player arrives at (210.0, 10.5, 205.0),
	// matching `210 205 y 11` read as x=210 z=205 y=11.] Polus omits the height entirely, which is why the runtime
	// ground-snaps a directive arrival that states none rather than throwing the X/Z away.
	//
	// Stored as ordinary `TravelDestination` rows, keyed by the dialogue EDGE whose pick leads to the directive
	// screen - which is exactly what that table already is ("the row is the conversation wiring"), so the trip runs
	// through the same capture-verified runtime the wagoners use rather than through a second one.
	public static class RdbDialogueTeleportImporter
	{
		// `{teleportplayer <x> <z> [y <y>] <map>}`. The coordinates are decimals (`55.7`), the map an integer.
		private static readonly Regex Teleport = new Regex(@"\{teleportplayer\s+(?<x>-?[\d.]+)\s+(?<z>-?[\d.]+)(?:\s+y\s+(?<y>-?[\d.]+))?\s+(?<map>\d+)\s*\}",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// `{delay <seconds>}` - how long the line stays up before the trip runs. Absent on Sigurd.
		private static readonly Regex Delay = new Regex(@"\{delay\s+(?<s>[\d.]+)\s*\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase)
		{
			ArgumentNullException.ThrowIfNull(db);

			Dictionary<(uint Category, uint Id), byte[]> ciphertext;
			using (var rdb = new RdbReader(rdbDir, cdnBase))
			{
				ciphertext = LdbStringIndex.EnglishCiphertext(rdb);
			}

			// AsNoTracking is load-bearing on a projection pulling the OWNED key type - EF cannot build an
			// identity-resolved owner for it and throws at query time otherwise.
			var screens = await db
				.Set<ConversationScreen>().AsNoTracking()
				.Select(s => new { s.ConversationNodeId, s.Category, s.ScreenId, s.Key })
				.ToListAsync();

			// The edge whose pick LANDS on a directive screen is where the row goes. A node reached by several edges
			// gets a row per edge: each of them is a way to buy the same trip.
			var edges = (await db
					.Set<ConversationEdge>().AsNoTracking()
					.Select(e => new { e.FromNodeId, e.OptionId, e.ToNodeId })
					.ToListAsync())
				.ToLookup(e => e.ToNodeId);

			var npcNames = await db.Set<ConversationNode>().AsNoTracking().ToDictionaryAsync(n => n.Id, n => n.NpcName);
			var knownMaps = await db.Maps.Select(m => m.Id).ToHashSetAsync();
			var existing = await db.TravelDestinations.ToDictionaryAsync(t => (t.FromNodeId, t.OptionId));

			var result = new Result { Screens = screens.Count };
			foreach (var screen in screens)
			{
				if (screen.Key.IsEmpty || !ciphertext.TryGetValue((screen.Category, screen.ScreenId), out var blob))
					continue; // no captured key, or the string is not on an English page

				var text = LdbKey.Decrypt(blob, screen.Key);
				if (text == null)
				{
					result.Unreadable++;
					continue;
				}

				var match = Teleport.Match(text);
				if (!match.Success)
					continue;

				result.Directives++;
				var mapId = uint.Parse(match.Groups["map"].Value, CultureInfo.InvariantCulture);
				if (!knownMaps.Contains(mapId))
				{
					// Loudly by count, not silently: a directive naming a playfield we have never imported is a trip
					// that would land the player in an empty world, and it is content we are missing rather than a
					// directive we misread.
					result.UnknownMap++;
					continue;
				}

				var inbound = edges[screen.ConversationNodeId].ToList();
				if (inbound.Count == 0)
				{
					// A directive screen no branch reaches cannot be keyed the way the table keys a trip. Counted so a
					// conversation-graph regression that orphans one shows up as a number rather than as silence.
					result.NoInboundEdge++;
					continue;
				}

				var x = float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
				var z = float.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture);
				float? y = match.Groups["y"].Success
					? float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture)
					: null;
				var delayMatch = Delay.Match(text);
				var delay = delayMatch.Success
					? float.Parse(delayMatch.Groups["s"].Value, CultureInfo.InvariantCulture)
					: 0f;

				foreach (var edge in inbound)
				{
					if (existing.TryGetValue((edge.FromNodeId, edge.OptionId), out var row))
					{
						// A captured trip already keyed here wins: it is the wire's own statement of where the player
						// landed, where this is the client's script for it. They should agree, and if they ever do not,
						// the capture is the truth source this project runs on.
						if (!row.FromDialogueDirective)
						{
							result.CapturedWins++;
							continue;
						}

						result.Updated++;
					}
					else
					{
						row = new TravelDestination { FromNodeId = edge.FromNodeId, OptionId = edge.OptionId };
						db.TravelDestinations.Add(row);
						existing[(edge.FromNodeId, edge.OptionId)] = row;
						result.Added++;
					}

					row.NpcName = npcNames.GetValueOrDefault(screen.ConversationNodeId) ?? "";
					row.DestinationMapId = mapId;
					row.ArrivalX = x;
					row.ArrivalY = y;
					row.ArrivalZ = z;
					row.Cost = null; // a directive traveller charges nothing - there is no fare and no trade window
					row.DelaySeconds = delay;
					row.FromDialogueDirective = true;
				}
			}

			await db.SaveChangesAsync();
			return result;
		}

		public sealed class Result
		{
			public int Screens { get; set; }
			public int Directives { get; set; }
			public int Added { get; set; }
			public int Updated { get; set; }
			public int CapturedWins { get; set; }
			public int NoInboundEdge { get; set; }
			public int UnknownMap { get; set; }
			public int Unreadable { get; set; }
		}
	}
}
