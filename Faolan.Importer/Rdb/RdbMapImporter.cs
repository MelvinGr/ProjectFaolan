using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports map names + ids from the client RDB PLAYFIELDDESCRIPTIONDATA (type 1070003). Each record is an XML
	// <playfielddescriptiondata> carrying <playfield Name=".." ID=".."/>; the RDB record id == the playfield id ==
	// our Map.Id (verified: 4010 Tortage Island, 2040 Castle of King Conan, 3000 Khemi, ...). Both v5-reachable
	// records (file < 0xFF) and the on-demand content-addressed ones (file == 0xFF) are imported: the latter are
	// pulled through the mirror proxy by RdbReader (cdnBaseUrl), so e.g. playfield 30200 - a real CDN-only playfield
	// that quest 19522 markers onto - now resolves. Names fill/replace Map.Name; missing maps are created as reference
	// rows (id + name only - no spawn transform / world burst, so they label the map without being enterable).
	public static class RdbMapImporter
	{
		private const uint PlayfieldDescriptionType = 1070003;
		private static readonly Regex PlayfieldRx = new Regex("<playfield\\s+Name=\"([^\"]*)\"\\s+ID=\"(\\d+)\"", RegexOptions.Compiled);

		// A handful of RDB playfield names carry internal level-tooling suffixes appended to the player-facing name
		// (e.g. "Khopshef Province Heigh_Tiles_Statels", "Wild Lands of Zelata_statel_height_painting",
		// "Old Tarantia Statels and Hmap"). Cut the name at the first such marker so .maps/teleport labels show the
		// clean name (#31). The markers are level-editor artifacts (statel = static element, hmap = height map),
		// never part of a real map name, so this can't truncate a legitimate one.
		private static readonly string[] ToolingMarkers = [" Heigh_Tiles", "_statel", " Statels", "_height", "_painting", " and Hmap", " Hmap"];

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase = null)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var maps = await db.Maps.ToDictionaryAsync(m => m.Id);

			int found = 0, gated = 0, created = 0, renamed = 0;
			foreach (var e in rdb.EntriesOfType(PlayfieldDescriptionType))
			{
				if (e.Size == 0)
					continue;

				// CDN (file==0xFF) records are pulled through the mirror; count the ones we can't reach (no proxy /
				// negative-cached miss) as gated so the summary still reports coverage.
				if (!rdb.CanRead(e))
				{
					gated++;
					continue;
				}

				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (dec.Length == 0) // CDN miss (404 / proxy down) - ReadRaw returns empty
				{
					gated++;
					continue;
				}

				var m = PlayfieldRx.Match(Encoding.Latin1.GetString(dec));
				if (!m.Success)
					continue;

				found++;
				var id = uint.Parse(m.Groups[2].Value);
				var name = SanitizeMapName(DecodeXmlEntities(m.Groups[1].Value));

				if (maps.TryGetValue(id, out var map))
				{
					if (map.Name != name)
					{
						map.Name = name;
						renamed++;
					}
				}
				else
				{
					var created2 = new Map
					{
						Id = id,
						Name = name
					};
					db.Maps.Add(created2);
					maps[id] = created2;
					created++;
				}
			}

			await db.SaveChangesAsync();
			return new Result(found, gated, created, renamed);
		}

		public static string SanitizeMapName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name;

			var cut = name.Length;
			foreach (var marker in ToolingMarkers)
			{
				var i = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
				if (i >= 0 && i < cut)
					cut = i;
			}

			return cut < name.Length
				? name[..cut].TrimEnd()
				: name;
		}

		// The playfield XML uses standard XML entities in names (e.g. Conall&apos;s Valley).
		private static string DecodeXmlEntities(string s)
		{
			return s.Replace("&apos;", "'").Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
		}

		public readonly record struct Result(int Found, int Gated, int Created, int Renamed);
	}
}
