using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Importer.Rdb
{
	// Imports resurrection points from the client RDB RESURRECTIONPOINTSEXPORT (type 1000090). These records are NOT
	// in the local NN.rdbdata files (le.idx file==0xFF); they are on-demand CDN content, fetched by the record's
	// 16-byte content hash = the CDN path md5: <gameupdate>/upm/rdb/res/<h0:2>/<h2:1>/<h3:>. The blob is the same
	// IOz1+zlib wrapper as local resources; the payload is XML <resurrectionpoint name discoverable><position x y z>.
	// The record id is the playfield/map id. discoverable="false" => AlwaysKnown (the starter). See
	// docs/archive/rdb-extraction-plan.md. Replaces all RespawnPoints with the real per-map data for the maps we have.
	public static class RdbRespawnImporter
	{
		public const string DefaultCdnBase = "http://aoc-eu-gameupdate.live.ageofconan.com";
		private const uint ResurrectionPointsType = 1000090;

		// Group 1 = Guid (the client resurrection-point id = the spawnPointId the 0x2024 release screen sends and the
		// client echoes in RespawnMe; #214), 2 = name, 3 = discoverable, 4/5/6 = position. Guid precedes name in the XML.
		private static readonly Regex PointRx =
			new Regex("<resurrectionpoint\\b[^>]*?\\bGuid=\"([0-9]+)\"[^>]*?\\bname=\"([^\"]*)\"[^>]*?\\bdiscoverable=\"([^\"]*)\"[^>]*?>\\s*<position\\s+x=\"([-0-9.]+)\"\\s+y=\"([-0-9.]+)\"\\s+z=\"([-0-9.]+)\"",
				RegexOptions.Compiled | RegexOptions.Singleline);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string cdnBase, string storeDir = null)
		{
			using var rdb = new RdbReader(rdbDir);
			using var cdn = new RdbCdnCache(cdnBase, storeDir);

			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();

			// Fresh import: drop existing points (cascade clears CharacterRespawnPoints) and rebuild from the CDN.
			db.CharacterRespawnPoints.RemoveRange(db.CharacterRespawnPoints);
			db.RespawnPoints.RemoveRange(db.RespawnPoints);
			await db.SaveChangesAsync();

			int maps = 0, points = 0, fetchFailed = 0, empty = 0;
			foreach (var e in rdb.EntriesOfType(ResurrectionPointsType).Where(e => ourMaps.Contains(e.Id)))
			{
				byte[] dec;
				try
				{
					dec = e.IsLocal
						? rdb.ReadDecompressed(e)
						: RdbReader.UnwrapIOz1Public(await cdn.FetchRawAsync(e.Hash));
				}
				catch
				{
					fetchFailed++;
					continue;
				}

				var xml = Encoding.Latin1.GetString(dec);
				var found = 0;
				foreach (Match m in PointRx.Matches(xml))
				{
					var guid = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
					var name = m.Groups[2].Value;
					var alwaysKnown = m.Groups[3].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
					var x = float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
					var y = float.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
					var z = float.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
					db.RespawnPoints.Add(new RespawnPoint
					{
						Id = guid, // the client RDB Guid - the spawnPointId the release screen needs (#214)
						MapId = e.Id,
						Name = name,
						AlwaysKnown = alwaysKnown,
						PositionX = x,
						PositionY = y,
						PositionZ = z
					});
					found++;
				}

				if (found > 0)
				{
					maps++;
					points += found;
				}
				else
					empty++;
			}

			await db.SaveChangesAsync();
			return new Result(maps, points, empty, fetchFailed);
		}

		public readonly record struct Result(int Maps, int Points, int Empty, int FetchFailed);
	}
}
