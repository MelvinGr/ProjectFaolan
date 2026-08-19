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

		// #672: the appearance entry's third word was read as "runtime state" and used to decline a whole morph
		// table, which spawned three NPCs BALD - Turach, Kincaid and Valkyrie Skalla. The classifier now accepts it
		// and `NpcSpawnMorph.Word2` carries it, and THIS is what makes that safe rather than a silent lie: a lifted
		// table has to survive the row -> wire trip with the field intact. Dropping the emit would leave the DB
		// truthful and the packet not, which is the state the ticket judged worse than the honest gap.
		[Fact]
		public void ASpawnCarryingWord2_RoundTripsItOntoTheWire()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));
			var carrying = ctx
				.NpcSpawns.Include(s => s.NpcTemplate)
				.ThenInclude(n => n.StatCore)
				.Include(s => s.Morphs)
				.AsNoTracking()
				.AsEnumerable()
				.Where(s => s.Morphs.Any(m => m.Word2 != 0))
				.ToList();

			// The shipped set, named so a re-harvest that loses one fails here rather than passing vacuously.
			Assert.Equal(["Kincaid", "Turach", "Valkyrie Skalla"], carrying.Select(s => s.NpcTemplate!.Name).Order().ToArray());

			var names = ctx.MorphTargets.AsNoTracking().ToDictionary(t => t.Id, t => t.Name);

			foreach (var row in carrying)
			{
				var expected = row.Morphs.OrderBy(m => m.Ordinal).Select(m => (m.MorphId, m.Word2)).ToArray();
				var built = SimpleCharFullUpdateCodec.Parse(NpcSpawnBuilder.BuildLive(row, 0x1000, 0x00A00001));

				var slot = built.Post.Slot;
				Assert.Equal(expected, slot.Appearance.Select(m => (m.MorphId, m.Word2)).ToArray());

				// The claim the acceptance rests on: the non-zero entries are a CONTIGUOUS PREFIX of the table, and
				// their values are a PERMUTATION of 1..N - dense, distinct, gapless. Runtime state accrued in play
				// would be scattered through the table and arbitrary in value; this is an ordinal of something.
				//
				// ⚠️ NOT in ascending order. #672's own write-up said "Turach's first three entries hold 1/2/3 and
				// Kincaid's first four hold 1/2/3/4", which reads as sorted and is not: Kincaid's are 3, 1, 2, 4. So
				// whatever it ordinals, it is not the table's own position, and asserting the sorted form would have
				// baked that misreading in.
				var nonZero = slot.Appearance.TakeWhile(m => m.Word2 != 0).Select(m => m.Word2).ToArray();
				Assert.NotEmpty(nonZero);
				Assert.Equal(Enumerable.Range(1, nonZero.Length).Select(i => (uint)i), nonZero.Order());
				Assert.All(slot.Appearance.Skip(nonZero.Length), m => Assert.Equal(0u, m.Word2));

				// ⭐ And WHAT it is attached to: every entry that carries it is a `*texscript_*` morph, i.e. a texture
				// script rather than a face/body slider or a hair mesh. That is the strongest single argument that it
				// is appearance data - runtime state would not confine itself to one KIND of entry - and it is why
				// the ordinal is not the table position: it orders the texscript run, whatever that ordering drives.
				var texscripts = names.Where(kv => kv.Value.StartsWith("*texscript", StringComparison.Ordinal)).Select(kv => kv.Key).ToHashSet();
				Assert.All(slot.Appearance.Where(m => m.Word2 != 0), m => Assert.Contains(m.MorphId, texscripts));
			}
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
