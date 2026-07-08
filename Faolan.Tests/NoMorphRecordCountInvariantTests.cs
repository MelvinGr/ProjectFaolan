using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #243 invariant: the fresh post-IIR recordCount of a NO-MORPH type (used to build its minimal form from scratch)
	// is read off ANY capture's header recordCount word - so it must be a SINGLE stable value per type across minimal
	// AND runtime-bearing captures. This guards NpcSpawnScaffold.TryReadNoMorphRecordCount (the importer's derivation
	// for the runtime-bearing tail that has no minimal-form capture); a type carrying >1 distinct recordCount, or one
	// outside {1,2}, would make that derivation ambiguous.
	public class NoMorphRecordCountInvariantTests
	{
		private readonly ITestOutputHelper _out;
		public NoMorphRecordCountInvariantTests(ITestOutputHelper o) => _out = o;

		[Fact]
		public void RecordCountWord_IsStablePerNoMorphType_AcrossMinimalAndRuntime()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			var rows = ctx.NpcSpawns
				.Include(s => s.Npc).ThenInclude(n => n.StatCore)
				.Where(s => s.Data != null && s.HasEquipment && s.Life > 0 && s.Npc.StatCore.IirHeader != null)
				.AsNoTracking().ToList();

			var rcByType = new Dictionary<string, SortedSet<uint>>();
			foreach (var row in rows)
			{
				var body = WorldObjectBuilder.ExtractBody(row.Data);
				if (body == null || !PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;
				// no-morph = neither the clean nor the lenient appearance table decodes
				if (NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out _) || NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out _))
					continue;
				var post = body.AsSpan(read.Offset + read.Length);
				if (post.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(post) != 0)
					continue;
				var rc = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(8, 4));
				var key = $"{row.Npc.Id}:{row.Npc.Name}";
				(rcByType.TryGetValue(key, out var set) ? set : rcByType[key] = new()).Add(rc);
			}

			var mixed = rcByType.Where(kv => kv.Value.Count > 1).ToList();
			var outOfRange = rcByType.Where(kv => kv.Value.Any(rc => rc is not (1 or 2))).ToList();
			_out.WriteLine($"no-morph types: {rcByType.Count}; mixed-recordCount: {mixed.Count}; out-of-range(not 1/2): {outOfRange.Count}");
			foreach (var (k, v) in mixed) _out.WriteLine($"  MIXED {k}: {{{string.Join(",", v)}}}");

			Assert.Empty(mixed);       // recordCount is a single stable value per no-morph type
			Assert.Empty(outOfRange);  // and always 1 or 2
		}

		// Every shipped spawn builds from typed columns - no NpcSpawn.Data fallback remains (#243/#244). Mirrors
		// TryBuildLive's from-scratch guard: an IIR header plus (no equipment | morph rows | a known recordCount).
		[Fact]
		public void EveryShippedSpawn_BuildsFromColumns_NoDataFallback()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			var rows = ctx.NpcSpawns
				.Include(s => s.Npc).ThenInclude(n => n.StatCore).Include(s => s.Morphs)
				.Where(s => s.Data != null && s.Npc != null && s.Npc.StatCore != null && s.Life > 0)
				.AsNoTracking().ToList();

			var fallback = rows.Where(s => !(s.Npc.StatCore.IirHeader is { }
				&& (!s.HasEquipment || s.Morphs.Count > 0 || s.Npc.StatCore.MinimalPostIirRecordCount is { }))).ToList();

			_out.WriteLine($"shipped={rows.Count} data-fallback={fallback.Count}");
			foreach (var s in fallback.Take(20)) _out.WriteLine($"  {s.Npc?.Name} map={s.MapId}");
			Assert.Empty(fallback);
		}

		private static string FindRepoRoot()
		{
			var d = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (d != null && !File.Exists(Path.Combine(d.FullName, "faolan.db")))
				d = d.Parent;
			return d?.FullName ?? throw new FileNotFoundException("faolan.db not found up-tree");
		}
	}
}
