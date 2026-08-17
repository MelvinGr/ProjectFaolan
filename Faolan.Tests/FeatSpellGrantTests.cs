using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #298: a learned spell is only castable once the character carries the FEAT that teaches it, and
	// `FeatSpellGrants` (GameAction element kind 53019 `Spell` on a feat template) is that mapping, read backwards by
	// `GetFeatsTeachingSpells`. What is gated here is that the grant stays inside what retail puts on the wire - the
	// feat list rides the player spawn's pre-IIR frame, so an extra id is a wire divergence, not a harmless surplus.
	public class FeatSpellGrantTests
	{
		// The PROGRESSION TRACKS: bases whose low digits run past the client's own rank ceiling
		// (FeatIdMap.MaxRank, from FUN_007850c0's `base*100 + 10`), so the digits index a family instead of naming a
		// rank. The twelve `<Class> Class Feat 001` tracks carry one entry per character level; the others are
		// `2HE 60-80`, `Bow 60-80` and `Class Specific HP 60-80`.
		// ⚠️ The gate that used to prove this from the captured feat lists is in `--report spell-grant-check` now
		// (#849). It read `db.CharacterFeats`, and `faolan.sql` excludes every `Character*` table as per-developer
		// play state, so the suite's fixture holds none: it asserted an empty join against an empty left side and
		// could not fail. Note the `Assert.NotEmpty(tracks)` it carried - the vacuity guard was on the RIGHT-hand
		// population, which was never the empty one.
		//
		// So the level-up grant must not train one either. Asked over the whole grant table at once, because the
		// per-class picture is what made this worth gating: a Dark Templar's level band resolves 32 of its spells
		// through `Spell Unlocking 20-40` (a real feat) and 5 through its class track (not one).
		[Fact]
		public async Task GetFeatsTeachingSpells_NeverReturnsAProgressionTrack()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var tracks = await TrackBases(db, ct);
			var allGranted = await db.FeatSpellGrants.AsNoTracking().Select(g => g.SpellId).Distinct().ToListAsync(ct);

			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			var feats = await repo.GetFeatsTeachingSpells(allGranted);
			Assert.NotEmpty(feats); // the mapping still resolves - the exclusion must not have emptied it

			var bases = await db.FeatIdMaps.AsNoTracking().Where(m => feats.Contains(m.WireFeatId)).Select(m => m.BaseIndex).Distinct().ToListAsync(ct);
			var leaked = bases.Where(tracks.Contains).ToList();
			Assert.True(leaked.Count == 0, "teaching feats on a progression-track base: " + string.Join(", ", leaked));
		}

		private static async Task<uint[]> TrackBases(DatabaseContext db, CancellationToken ct)
		{
			return await db.FeatIdMaps.AsNoTracking()
				.GroupBy(m => m.BaseIndex)
				.Where(g => g.Max(m => m.Rank) >= FeatIdMap.MaxRank)
				.Select(g => g.Key)
				.ToArrayAsync(ct);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
