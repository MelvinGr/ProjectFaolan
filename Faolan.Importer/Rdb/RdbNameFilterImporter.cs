using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Progression;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the client's forbidden-word list (RDB 1065100) into `NameFilterTerm` as HASHES - the words themselves
	// must not land in a repo that goes public, and `faolan.sql` mirrors every row as reviewable text. See
	// `NameFilterTerm` and `CharacterNameRules` for why hashing costs the check nothing.
	//
	// The list is deduped (most terms are listed twice) and filtered to terms that can actually be a substring of a
	// character name: 3..16 characters, no separators.
	public static class RdbNameFilterImporter
	{
		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase = null, bool overwrite = false)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var terms = RdbNameFilterList.Load(rdb);

			var usable = terms
				.Select(t => t.Trim())
				.Where(CharacterNameRules.IsUsableTerm)
				.Select(t => (Hash: CharacterNameRules.Hash(t), t.Length))
				.DistinctBy(t => t.Hash)
				.ToList();

			var existing = await db.NameFilterTerms.ToDictionaryAsync(t => t.TermHash);
			if (overwrite)
			{
				db.NameFilterTerms.RemoveRange(existing.Values);
				existing = [];
			}

			var added = 0;
			foreach (var (hash, length) in usable)
			{
				if (existing.ContainsKey(hash))
					continue;

				db.NameFilterTerms.Add(new NameFilterTerm { TermHash = hash, Length = length });
				added++;
			}

			await db.SaveChangesAsync();

			return new Result(terms.Count, usable.Count, added);
		}

		// Shipped = what the record carries, Usable = what survives dedupe + the substring-shaped filter,
		// Added = rows written.
		public sealed record Result(int Shipped, int Usable, int Added);
	}
}
