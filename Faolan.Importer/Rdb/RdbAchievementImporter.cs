using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// The ACHIEVEMENT catalog (#748), read straight out of the client's LDB: category 55701 is the achievement's NAME
	// and 55700 what earns it, both keyed by the id the wire calls a TAG (`GameCharInterface.DispatchAddTag`, one u32).
	//
	// That keying is what makes this a decode rather than a guess: every tag the corpus awards resolves, and resolves to
	// something that matches what the capture was DOING. 3509 "Pet Collector" / *"Collect and summon your first pet"* is
	// awarded in `..._login_summon_pet` at the instant a pet is summoned; 716 "Old Tarantia Docks" the instant the vista
	// fly-through of #740 ends; 2562 "Spider Caverns" / *"Defeat the Lurking Horror in the Spider Caverns"* beside 2563
	// "Lurking Horror".
	//
	// English pages only, decided by the client's own language records (`LdbLanguageIndex`) rather than by scoring the
	// prose - the mistake RdbLdbNameImporter documents, where untranslated pages score as English and the last write
	// wins.
	public static class RdbAchievementImporter
	{
		private const uint LdbType = 1030002;

		public sealed record Result(int Names, int Descriptions, int Written);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string proxyBase)
		{
			using var rdb = new RdbReader(rdbDir, proxyBase);

			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);
			var names = new Dictionary<uint, string>();
			var descriptions = new Dictionary<uint, string>();

			foreach (var e in rdb.EntriesOfType(LdbType).Where(rdb.CanRead))
			{
				if (!languageByPage.TryGetValue(e.Id, out var language) || language != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not an IOz1/zlib page
				}

				var target = LdbStringIndex.CategoryOf(body) switch
				{
					LdbStringIndex.AchievementNameCategory => names,
					LdbStringIndex.AchievementDescriptionCategory => descriptions,
					_ => null
				};
				if (target == null)
					continue;

				foreach (var entry in LdbStringIndex.Parse(body))
				{
					if (!string.IsNullOrWhiteSpace(entry.Value))
						target[entry.Id] = entry.Value;
				}
			}

			// The NAME is the identity: an id with only a description is not an achievement we can name, so it is not a
			// row. The reverse is normal and kept - about half the catalog ships no description.
			var existing = await db.Achievements.ToDictionaryAsync(a => a.Id);
			var written = 0;
			foreach (var (id, name) in names)
			{
				var description = descriptions.GetValueOrDefault(id);
				if (existing.TryGetValue(id, out var row))
				{
					if (row.Name == name && row.Description == description)
						continue;

					row.Name = Truncate(name, 128);
					row.Description = description is null ? null : Truncate(description, 512);
					written++;
					continue;
				}

				db.Achievements.Add(new Achievement
				{
					Id = id,
					Name = Truncate(name, 128),
					Description = description is null ? null : Truncate(description, 512)
				});
				written++;
			}

			await db.SaveChangesAsync();
			return new Result(names.Count, descriptions.Count, written);
		}

		// The LDB is not bound by our column widths, and a string longer than the column is a write that would throw at
		// the very end of a long import. Cut rather than drop: a clipped title still names the achievement.
		private static string Truncate(string value, int max)
		{
			return value.Length <= max ? value : value[..max];
		}
	}
}
