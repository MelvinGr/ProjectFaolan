using System.IO;
using System.Linq;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #243/#244: every shipped NPC spawn builds its FA015807 body wholly from typed columns - the captured
	// NpcSpawn.Data blob has been retired, so this is the gate that nothing regressed back into needing it.
	public class NoMorphRecordCountInvariantTests
	{
		private readonly ITestOutputHelper _out;
		public NoMorphRecordCountInvariantTests(ITestOutputHelper o) => _out = o;

		// Mirrors TryBuildLive's build guard: a lifted type (StatCore.IirHeader) plus one of - no equipment (animal
		// -> minimal post-IIR), morph rows (-> appearance table), or a known minimal recordCount (no-morph spawn ->
		// minimal form). Any shipped spawn that fails all three would have no way to build without the dropped Data.
		[Fact]
		public void EveryShippedSpawn_BuildsFromColumns()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);
			var rows = ctx.NpcSpawns
				.Include(s => s.Npc).ThenInclude(n => n.StatCore).Include(s => s.Morphs)
				.Where(s => s.Npc != null && s.Npc.StatCore != null && s.Life > 0)
				.AsNoTracking().ToList();

			var fallback = rows.Where(s => !(s.Npc.StatCore.IirHeader is { }
				&& (!s.HasEquipment || s.Morphs.Count > 0 || s.Npc.StatCore.MinimalPostIirRecordCount is { }))).ToList();

			_out.WriteLine($"shipped={rows.Count} not-buildable-from-columns={fallback.Count}");
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
