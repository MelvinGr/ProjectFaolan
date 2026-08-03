using System;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #243/#244: every shipped NPC spawn builds its FA015807 body wholly from typed columns - the captured
	// NpcSpawn.Data blob has been retired, so this is the gate that nothing regressed back into needing it.
	public class SpawnBuildsFromColumnsTests
	{
		private readonly ITestOutputHelper _out;

		public SpawnBuildsFromColumnsTests(ITestOutputHelper o)
		{
			_out = o;
		}

		// Mirrors TryBuildLive's build guard, which is now the single condition `StatCore.IirHeader is not null`.
		// It used to carry a third arm - an equipped no-morph humanoid also needed a derived post-IIR word2 - and
		// that arm is gone because the word2 is gone: it was the movement mode under another name, so the post-IIR
		// takes it from the character message and no separate column has to have been derived (#605).
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

			var fallback = rows.Where(s => s.NpcTemplate.StatCore!.IirHeader is null).ToList();

			_out.WriteLine($"shipped={rows.Count} not-buildable-from-columns={fallback.Count}");
			foreach (var s in fallback.Take(20))
				_out.WriteLine($"  {s.NpcTemplate?.Name} map={s.MapId}");
			Assert.Empty(fallback);
		}

		// The wire invariant the whole of #605 comes down to: a spawn body carries its movement mode TWICE - as the
		// character message's `movement_mode` and as the post-IIR header's third word - and retail sends the same
		// number in both. Measured over the corpus at 5,738/5,739 NPC spawns and 209/209 player spawns
		// (`--report field-predictors`); here it is asserted of what WE emit, which holds exactly because the builder
		// derives one from the other. Before that, the two came from two separate columns lifted from two different
		// representative spawns, and 45 of the 368 shipped types disagreed with themselves.
		[Fact]
		public void EveryShippedSpawn_SendsOneMovementModeInBothPlaces()
		{
			var db = TestPaths.FaolanDb;
			var options = TestDb.ReadOnly(db);
			using var ctx = new DatabaseContext(options);
			var rows = ctx
				.NpcSpawns.Include(s => s.NpcTemplate)
				.ThenInclude(n => n.StatCore)
				.Include(s => s.Morphs)
				.Include(s => s.Equipment)
				.ThenInclude(e => e.Item)
				.Where(s => s.NpcTemplate != null && s.NpcTemplate.StatCore != null && s.NpcTemplate.StatCore.IirHeader != null && s.Life > 0)
				.AsNoTracking()
				.ToList();

			Assert.NotEmpty(rows);
			var mismatches = 0;
			foreach (var row in rows)
			{
				var body = NpcSpawnBuilder.BuildLive(row, 0x1310001, 0x00A00001);
				var spawn = SimpleCharFullUpdateCodec.Parse(body.AsSpan());
				var message = spawn.Character.ShouldSerializeMovementMode()
					? spawn.Character.MovementMode
					: SpawnPostIirs.DefaultMovementMode;
				if (message == spawn.Post.MovementMode)
					continue;

				mismatches++;
				if (mismatches <= 20)
					_out.WriteLine($"  {row.NpcTemplate!.Name}: message={message} post-IIR={spawn.Post.MovementMode}");
			}

			_out.WriteLine($"built={rows.Count} mismatches={mismatches}");
			Assert.Equal(0, mismatches);
		}
	}
}
