using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The world-object geometry record's six-word "node" is a STANDING PARTICLE EFFECT and the bone it hangs on
	// (#687), not kd-tree geometry:
	//
	//     [effect] [0] [0] [0] [0] [attractor]
	//
	// These gates pin the two words that ARE named, and - just as importantly - that the other four are still
	// zero. The moment a capture lands one that is not, the middle of this record becomes decidable and the
	// `[V0:]` on it can go; until then, naming those four would be inventing their order.
	public class WorldObjectParticleNodeTests
	{
		// The client's attachment-point values, shared with SimpleDynelVisualChangeCodec.StartParticleEffect's
		// `attractor` slot. 0 there means "free-standing at the given position"; a node always attaches.
		private static readonly uint[] Attractors = [34, 35];

		[Fact]
		public async Task ANodeIsSixWords()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var byObject = await db
				.WorldObjectWords.AsNoTracking()
				.Where(w => w.Kind == WorldObjectWordKind.GeometryNode)
				.GroupBy(w => w.WorldObjectId)
				.Select(g => new { g.Key, Count = g.Count() })
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(byObject);
			Assert.All(byObject, o => Assert.Equal(0, o.Count % 6));
		}

		// Ordinal 0 is the effect. Every shipped value was checked against the client's own RDB and resolves to a
		// 1010207 (particle-effect) record - that check needs the RDB, so what is gated here is the SHAPE: the ids
		// are large, distinct, and none of them is a small enum-looking number that would suggest something else.
		[Fact]
		public async Task TheEffectSlot_HoldsResourceIds_NotSmallEnums()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var effects = await Slot(db, 0).ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(effects);
			Assert.All(effects, e => Assert.True(e > 1_000_000, $"effect id {e} is too small to be an RDB resource id"));
		}

		// The middle four are zero throughout, which is exactly why the rotation/offset order cannot be settled.
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		public async Task TheMiddleFour_AreStillAllZero(int ordinal)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var values = await Slot(db, ordinal).Distinct().ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal([0u], values);
		}

		// Ordinal 5 is the ATTRACTOR, and this is the evidence for it: it takes exactly the client's two non-zero
		// attachment-point values and nothing else. A coordinate would not be confined to two integers.
		[Fact]
		public async Task TheAttractorSlot_TakesOnlyTheClientsAttachmentPoints()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var values = await Slot(db, 5).Distinct().ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(values);
			Assert.All(values, v => Assert.Contains(v, Attractors));
		}

		// ...and it is CONSTANT per effect, which a coordinate could not be either: 4998690 and 3139195 always hang
		// on 34, the other nine effects always on 35. An attachment point is a property of the effect, so this is
		// the second, independent reason the slot is what it is called.
		[Fact]
		public async Task EachEffect_AlwaysHangsOnTheSameAttractor()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var words = await db
				.WorldObjectWords.AsNoTracking()
				.Where(w => w.Kind == WorldObjectWordKind.GeometryNode && (w.Ordinal % 6 == 0 || w.Ordinal % 6 == 5))
				.ToListAsync(TestContext.Current.CancellationToken);

			var pairs = words
				.GroupBy(w => (w.WorldObjectId, Node: w.Ordinal / 6))
				.Where(g => g.Count() == 2)
				.Select(g => (Effect: g.First(w => w.Ordinal % 6 == 0).Value, Attractor: g.First(w => w.Ordinal % 6 == 5).Value))
				.ToList();

			Assert.NotEmpty(pairs);
			foreach (var effect in pairs.GroupBy(p => p.Effect))
				Assert.True(effect.Select(p => p.Attractor).Distinct().Count() == 1,
					$"effect {effect.Key} hangs on {effect.Select(p => p.Attractor).Distinct().Count()} different attractors - it is not an attachment point after all");
		}

		private static IQueryable<uint> Slot(DatabaseContext db, int ordinal)
		{
			return db.WorldObjectWords.AsNoTracking()
				.Where(w => w.Kind == WorldObjectWordKind.GeometryNode && w.Ordinal % 6 == ordinal)
				.Select(w => w.Value);
		}
	}
}
