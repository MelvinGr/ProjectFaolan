using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer
{
	// Imports the client's character-customization registry (RDB type 1000076 id 0) into MorphTarget rows.
	// The registry lists every selectable appearance target - morph sliders and the choosable resources
	// (*hairmesh/*beardmesh/*piercingmesh/*texscript) - with a category, a weight and a 1-based id. The spawn
	// scaffold's weighted entry-table records carry id-1 as their tag, so rows are stored under that wire tag.
	// (Cracked 2026-06-10: faolanx's 7 weighted spawn entries resolve 7/7 against his editor resource refs;
	// see docs/player-spawn-generation-plan.md "Weighted attributes".)
	public static class RdbMorphTargetImporter
	{
		private const uint RegistryType = 1000076;

		public static async Task<int> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
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

		// Registry payload: [u32 type][u32 0][u32 ver=1][u32 count][u32 2][u32 ?] then count records of
		// [u32 nameLen][name][u32 category][u32 defaultBits][f32 weight][u32 id]. The last record can be
		// truncated mid-stream in the shipped client (the final *texscript record lacks its id) - such a
		// tail is skipped.
		public static List<MorphTarget> ParseRegistry(byte[] payload)
		{
			var targets = new List<MorphTarget>();
			var i = 24;
			while (i + 4 <= payload.Length)
			{
				var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i, 4));
				if (nameLen <= 0 || nameLen > 64 || i + 4 + nameLen + 16 > payload.Length)
					break;

				var name = Encoding.ASCII.GetString(payload, i + 4, nameLen);
				i += 4 + nameLen;

				var category = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i, 4));
				var weight = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i + 8, 4)));
				var id = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i + 12, 4));
				i += 16;

				if (id == 0)
					continue;

				targets.Add(new MorphTarget
				{
					Id = id - 1, // the wire tag
					Name = name,
					Category = (byte)category,
					Weight = weight
				});
			}

			return targets;
		}
	}
}
