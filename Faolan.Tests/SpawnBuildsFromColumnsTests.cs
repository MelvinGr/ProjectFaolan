using System;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Every shipped NPC spawn builds its FA015807 body wholly from typed columns - there is no captured
	// NpcSpawn.Data blob to fall back on, and this is the gate that nothing regresses back into needing one.
	public class SpawnBuildsFromColumnsTests
	{
		private readonly ITestOutputHelper _out;

		public SpawnBuildsFromColumnsTests(ITestOutputHelper o)
		{
			_out = o;
		}

		// Mirrors TryBuildLive's build guard, the single condition `StatCore.IirHeader is not null`. An equipped
		// no-morph humanoid needs no extra arm: its post-IIR word2 is the movement mode under another name, so the
		// post-IIR takes it from the character message and no separate column has to be derived.
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

		// An epic copy serves the normal population wearing the elite decorations - the builder's
		// normalsetOverride retags `NpcStats.normalset` exactly as the back-to-back Spider Caverns captures show
		// (regulars absent -> 13, minions 1 -> 9, zero exceptions over 171 spawns), and the default build keeps the
		// stored set so a normal zone is untouched.
		[Fact]
		public void BuildLive_EpicNormalsetOverride_RetagsTheDecoration_AndDefaultKeepsTheStoredOne()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));
			var row = ctx
				.NpcSpawns.Include(s => s.NpcTemplate)
				.ThenInclude(n => n.StatCore)
				.Include(s => s.Morphs)
				.AsNoTracking()
				.First(s => s.NpcTemplate!.Name == "Cavecrawler");

			Assert.Equal(4u, row.NpcTemplate!.StatCore!.NpcType); // a regular mob - the measured elite retag applies
			var stored = row.NpcTemplate.StatCore.Normalset;
			Assert.Equal(EpicDecorations.EliteNormalset, EpicDecorations.NormalsetFor(row.NpcTemplate.StatCore));

			var normal = SimpleCharFullUpdateCodec.Parse(NpcSpawnBuilder.BuildLive(row, 0x1000, 0x00A00001));
			var epic = SimpleCharFullUpdateCodec.Parse(NpcSpawnBuilder.BuildLive(row, 0x1000, 0x00A00001, normalsetOverride: EpicDecorations.NormalsetFor(row.NpcTemplate.StatCore)));

			Assert.Equal(stored ?? 0u, normal.Character.npc_stats?.Normalset ?? 0u);
			Assert.Equal(EpicDecorations.EliteNormalset, epic.Character.npc_stats!.Normalset);

			// Mini-bosses/bosses are unmeasured in the epic window, so the policy leaves them alone.
			Assert.Null(EpicDecorations.NormalsetFor(new NpcStatCore { NpcType = 3 }));
		}

		// The appearance entry's third word is not runtime state: read as such it declines a whole morph table and
		// spawns NPCs bald (Turach, Kincaid and Valkyrie Skalla). `NpcSpawnMorph.Word2` carries it, and this is what
		// makes that safe rather than a silent lie: a lifted table has to survive the row -> wire trip with the field
		// intact. Dropping the emit would leave the DB truthful and the packet not, which is worse than an honest gap.
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

				// The claim the acceptance rests on: the non-zero entries are a contiguous prefix of the table, and
				// their values are a permutation of 1..N - dense, distinct, gapless. Runtime state accrued in play
				// would be scattered through the table and arbitrary in value; this is an ordinal of something.
				//
				// Not in ascending order: Turach's first three entries hold 1/2/3 but Kincaid's first four hold 3, 1, 2, 4.
				// So whatever it ordinals, it is not the table's own position, and asserting the sorted form would bake that
				// misreading in.
				var nonZero = slot.Appearance.TakeWhile(m => m.Word2 != 0).Select(m => m.Word2).ToArray();
				Assert.NotEmpty(nonZero);
				Assert.Equal(Enumerable.Range(1, nonZero.Length).Select(i => (uint)i), nonZero.Order());
				Assert.All(slot.Appearance.Skip(nonZero.Length), m => Assert.Equal(0u, m.Word2));

				// And what it is attached to: every entry that carries it is a `*texscript_*` morph, i.e. a texture
				// script rather than a face/body slider or a hair mesh. That is the strongest single argument that it
				// is appearance data - runtime state would not confine itself to one kind of entry - and it is why
				// the ordinal is not the table position: it orders the texscript run, whatever that ordering drives.
				var texscripts = names.Where(kv => kv.Value.StartsWith("*texscript", StringComparison.Ordinal)).Select(kv => kv.Key).ToHashSet();
				Assert.All(slot.Appearance.Where(m => m.Word2 != 0), m => Assert.Contains(m.MorphId, texscripts));
			}
		}

		// A spawn body carries its movement mode twice - as the character message's `movement_mode` and as the
		// post-IIR header's third word - and retail sends the same number in both. Measured over the corpus at
		// 5,738/5,739 NPC spawns and 209/209 player spawns (`--report field-predictors`); here it is asserted of what
		// we emit, which holds exactly because the builder derives one from the other. Lifting the two from separate
		// columns instead leaves types disagreeing with themselves.
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
