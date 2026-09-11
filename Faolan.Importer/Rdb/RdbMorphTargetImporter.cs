using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the client's character-customization registry (RDB type 1000076 id 0) into MorphTarget rows.
	// The registry lists every selectable appearance target - morph sliders and the choosable resources
	// (*hairmesh/*beardmesh/*piercingmesh/*texscript) - with a category, a weight and a 1-based id. The spawn
	// scaffold's weighted entry-table records carry id-1 as their tag, so rows are stored under that wire tag.
	// (Cracked 2026-06-10: faolanx's 7 weighted spawn entries resolve 7/7 against his editor resource refs;
	// see docs/re/player-spawn-generation.md "Weighted attributes".)
	public static class RdbMorphTargetImporter
	{
		private const uint RegistryType = 1000076;

		public static async Task<int> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var entry = rdb.EntriesOfType(RegistryType).FirstOrDefault();
			if (entry.Size == 0)
			{
				Console.WriteLine($"RDB morph targets: registry type {RegistryType} not found; skipping.");
				return 0;
			}

			var targets = ParseRegistry(rdb.ReadDecompressed(entry));

			await db.MorphTargets.ExecuteDeleteAsync();
			db.MorphTargets.AddRange(targets);
			var n = await db.SaveChangesAsync();
			Console.WriteLine($"RDB morph targets: imported {targets.Count} registry entries (categories {targets.Min(t => t.Category)}-{targets.Max(t => t.Category)}).");
			return n;
		}

		// Registry payload: [12 bytes][u32 count][u32 version==2] then count records of
		// [u32 id][u32 nameLen][name][u32 category][f32 default][f32 weight]. The id LEADS its record and is
		// the wire tag - it is not offset.
		//
		// Recovered from the client's own parser (`e_RDB_MorphNameIDs`, FUN_0103f380, reached from the
		// LoadStaticData registration for RDB type 1000076): it skips 12, reads count, asserts version 2, then
		// per record reads a u32, a string, a u32 into +0x1c and two floats into +0x20/+0x24 - the u32 it reads
		// first is what it keys the entry by.
		//
		// Start at the record's own id, never one field in. Skipping record 0's id and subtracting one from the next
		// is a one-field shift that mostly cancels - ids ascend densely, so `next - 1` is right almost everywhere and
		// a handful of captured tags validate - but it breaks wherever the sequence jumps: `*texscript_4747783` then
		// takes 590 (the id of the *beardmesh that follows it, minus one) instead of its own, and the client draws a
		// beard for a skin-colour tag. It also makes the last record look truncated, because the id such a walk goes
		// looking for belongs to a record that does not exist.
		public static List<MorphTarget> ParseRegistry(byte[] payload)
		{
			var targets = new List<MorphTarget>();
			var seen = new HashSet<uint>();
			var i = 20;
			while (i + 8 <= payload.Length)
			{
				var id = payload.ReadU32Le(i);
				var nameLen = (int)payload.ReadU32Le(i + 4);
				if (nameLen <= 0 || nameLen > 64 || i + 8 + nameLen + 12 > payload.Length)
					break;

				var name = Encoding.ASCII.GetString(payload, i + 8, nameLen);
				i += 8 + nameLen;

				var category = payload.ReadU32Le(i);
				var weight = BitConverter.UInt32BitsToSingle(payload.ReadU32Le(i + 8));
				i += 12;

				// The id is the primary key, so a repeat would be a silent overwrite of somebody else's entry. Never
				// observed; loud if the layout ever shifts again.
				if (!seen.Add(id))
					throw new InvalidOperationException($"RDB 1000076: duplicate morph-target id {id} (second holder '{name}') - the record layout no longer matches the client's parser.");

				targets.Add(new MorphTarget
				{
					Id = id, // the wire tag, verbatim
					Name = name,
					Category = (byte)category,
					Weight = weight
				});
			}

			return targets;
		}
	}
}
