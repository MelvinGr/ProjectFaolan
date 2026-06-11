using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Imports map names + ids from the client RDB PLAYFIELDDESCRIPTIONDATA (type 1070003). Each record is an XML
	// <playfielddescriptiondata> carrying <playfield Name=".." ID=".."/>; the RDB record id == the playfield id ==
	// our Map.Id (verified: 4010 Tortage Island, 2040 Castle of King Conan, 3000 Khemi, ...). v5-reachable records
	// (file < 0xFF) are read now; the v6 content-addressed ones (file == 0xFF) are skipped until the v6 index is
	// cracked (docs/rdb-extraction-plan.md). Names fill/replace Map.Name; missing maps are created as reference rows
	// (id + name only - no spawn transform / world burst, so they label the map without being enterable).
	public static class RdbMapImporter
	{
		private const uint PlayfieldDescriptionType = 1070003;
		private static readonly Regex PlayfieldRx = new("<playfield\\s+Name=\"([^\"]*)\"\\s+ID=\"(\\d+)\"", RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var maps = await db.Maps.ToDictionaryAsync(m => m.Id);

			int found = 0, gated = 0, created = 0, renamed = 0;
			foreach (var e in rdb.EntriesOfType(PlayfieldDescriptionType))
			{
				if (e.Size == 0)
					continue;
				if (e.File == 0xff) // v6 content-addressed - not in the v5 NN.rdbdata files
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

				var m = PlayfieldRx.Match(Encoding.Latin1.GetString(dec));
				if (!m.Success)
					continue;

				found++;
				var id = uint.Parse(m.Groups[2].Value);
				var name = DecodeXmlEntities(m.Groups[1].Value);

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
					var created2 = new Map { Id = id, Name = name };
					db.Maps.Add(created2);
					maps[id] = created2;
					created++;
				}
			}

			await db.SaveChangesAsync();
			return new Result(found, gated, created, renamed);
		}

		// The playfield XML uses standard XML entities in names (e.g. Conall&apos;s Valley).
		private static string DecodeXmlEntities(string s)
		{
			return s
				.Replace("&apos;", "'")
				.Replace("&quot;", "\"")
				.Replace("&lt;", "<")
				.Replace("&gt;", ">")
				.Replace("&amp;", "&");
		}

		public readonly record struct Result(int Found, int Gated, int Created, int Renamed);
	}
}
