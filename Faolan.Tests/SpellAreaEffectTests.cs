using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #772: the shipped `SpellAreaEffects` rows against IN-CLIENT TOOLTIPS, which is the only oracle that can say the
	// decode is right rather than merely self-consistent. Every number below was read off a screenshot before the
	// import existed.
	//
	// The distinction these gate is the one the whole ticket turned on: a spell and its EFFECT are different records.
	// `Life Leech` 220979 is the castable (cast time, mana, recast) and 220980 is what lands. Applying the castable is
	// what made the client answer `ActivateBuff 220979 remaining time 0` and drop the cast (#767).
	public class SpellAreaEffectTests
	{
		// `Life Leech` - "Area Of Effect: Life Leech 5.00m radius up to 12 targets", applied ONCE at cast.
		[Fact]
		public async Task LifeLeech_AppliesItsEffectRecordOnceInFiveMetres()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var areas = await db.SpellAreaEffects.AsNoTracking().Where(a => a.SpellId == 220979).OrderBy(a => a.Sequence).ToListAsync(TestContext.Current.CancellationToken);

			var area = Assert.Single(areas);
			Assert.Equal(220980u, area.EffectSpellId); // the EFFECT record, not the castable
			Assert.Equal(5u, area.RadiusMetres);
			Assert.Equal(12u, area.MaxTargets);
			Assert.Equal(0u, area.PeriodCentiseconds); // instant: no aura
		}

		// `Gangrenous Stench` - "7.00m radius up to 12 targets" TWICE: once as the Area Of Effect, once as an Effect
		// over time "every 4 seconds". The second row is the aura, and it is the row a design that put these on the
		// parent spell would have had to drop.
		[Fact]
		public async Task GangrenousStench_CarriesBothAnInstantAreaAndA4SecondAura()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var areas = await db.SpellAreaEffects.AsNoTracking().Where(a => a.SpellId == 221070).OrderBy(a => a.Sequence).ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(2, areas.Count);
			Assert.All(areas, a =>
			{
				Assert.Equal(221069u, a.EffectSpellId);
				Assert.Equal(7u, a.RadiusMetres);
				Assert.Equal(12u, a.MaxTargets);
			});

			Assert.Equal(0u, areas[0].PeriodCentiseconds);
			Assert.Equal(400u, areas[1].PeriodCentiseconds); // "every 4 seconds"
		}

		// The chain continues one level down, which is what makes it an AURA rather than a one-shot: the effect
		// 221069 applies 221068 in a 4.00m radius every 3 seconds ("Effect over time: Gangrenous Stench 4.00m radius
		// every 3 seconds", the second tooltip block).
		[Fact]
		public async Task TheEffectRecordItself_CarriesA3SecondArea()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var area = await db.SpellAreaEffects.AsNoTracking().SingleAsync(a => a.SpellId == 221069, TestContext.Current.CancellationToken);

			Assert.Equal(221068u, area.EffectSpellId);
			Assert.Equal(4u, area.RadiusMetres);
			Assert.Equal(300u, area.PeriodCentiseconds);
		}

		// A row that names its own spell is a self-reference the importer must not emit - it would make the aura tick
		// re-apply the carrier to itself for ever.
		[Fact]
		public async Task NoAreaEffect_PointsAtItsOwnSpell()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var self = await db.SpellAreaEffects.AsNoTracking().CountAsync(a => a.EffectSpellId == a.SpellId, TestContext.Current.CancellationToken);

			Assert.Equal(0, self);
		}

		// A period is a real cadence, not a flag: anything under a tick of the world loop would be a busy-apply, and a
		// zero radius would make the area unhittable. Both are shape claims about the DECODE - if either breaks, the
		// slot being read as the period or the radius is the wrong one.
		[Fact]
		public async Task EveryAura_HasAPlausiblePeriodAndRadius()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.SpellAreaEffects.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(rows);

			// A single row carries no radius, and it is a `ConeFormula` (53259) - a cone states two angles and may
			// leave its reach to the client. Pinned as a small population rather than asserted away, so it cannot
			// quietly grow into "the radius slot moved".
			Assert.InRange(rows.Count(a => a.RadiusMetres == 0), 0, 20);
			Assert.All(rows.Where(a => a.RadiusMetres == 0), a => Assert.Equal(53259u, a.Kind));
			Assert.All(rows.Where(a => a.RadiusMetres > 0), a => Assert.InRange(a.RadiusMetres, 1u, 100000u));
			Assert.All(rows.Where(a => a.PeriodCentiseconds > 0), a => Assert.InRange(a.PeriodCentiseconds, 10u, 360000u));
		}
	}
}
