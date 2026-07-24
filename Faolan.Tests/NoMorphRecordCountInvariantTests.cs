using System.Linq;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #243/#244: every shipped NPC spawn builds its FA015807 body wholly from typed columns - the captured
	// NpcSpawn.Data blob has been retired, so this is the gate that nothing regressed back into needing it.
	public class NoMorphRecordCountInvariantTests
	{
		private readonly ITestOutputHelper _out;

		public NoMorphRecordCountInvariantTests(ITestOutputHelper o)
		{
			_out = o;
		}

		// Mirrors TryBuildLive's build guard: a lifted type (StatCore.IirHeader) plus one of - no equipment (animal
		// -> minimal post-IIR), morph rows (-> appearance table), or a known minimal recordCount (no-morph spawn ->
		// minimal form). Any shipped spawn that fails all three would have no way to build without the dropped Data.
		[Fact]
		public void EveryShippedSpawn_BuildsFromColumns()
		{
			var db = TestPaths.FaolanDb;
			var options = TestDb.ReadOnly(db);
			using var ctx = new DatabaseContext(options);
			var rows = ctx
				.NpcSpawns.Include(s => s.NpcTemplate)
				.ThenInclude(n => n.StatCore)
				.Include(s => s.Morphs)
				.Where(s => s.NpcTemplate != null && s.NpcTemplate.StatCore != null && s.Life > 0)
				.AsNoTracking()
				.ToList();

			var fallback = rows
				.Where(s => !(s.NpcTemplate.StatCore!.IirHeader is not null && (!s.HasEquipment || s.Morphs.Count > 0 || s.NpcTemplate.StatCore.MinimalPostIirRecordCount is not null)))
				.ToList();

			_out.WriteLine($"shipped={rows.Count} not-buildable-from-columns={fallback.Count}");
			foreach (var s in fallback.Take(20))
				_out.WriteLine($"  {s.NpcTemplate?.Name} map={s.MapId}");
			Assert.Empty(fallback);
		}
	}
}
