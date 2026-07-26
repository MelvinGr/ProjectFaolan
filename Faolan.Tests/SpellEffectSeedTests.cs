using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Modules;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The SpellEffect seed (#439): the decoded facts that used to be hardcoded spell ids in the server - the necro's
	// pet summons and Frozen Hatred's fatality proc - now live as rows, so these gate the rows rather than constants.
	public class SpellEffectSeedTests
	{
		// The 12 summonable necro pets, from NecroL80_..._mount-horse_all-necro-summons_dismiss-all (every one summoned
		// and mass-dismissed in that session). A summon is a cast that resolves to a spawn, so each row is (cast id ->
		// creature type); "Pleasurable Company" is the one non-combat companion.
		[Fact]
		public void Seed_CarriesTheTwelveCapturedPetSummons()
		{
			var summons = SpellEffectsModule.BuildRows().Where(r => r.Trigger == SpellEffectTrigger.OnCast && r.Kind == SpellEffectKind.SummonPet).ToList();

			Assert.Equal(12, summons.Count);
			Assert.Equal("Arcanist", summons.Single(s => s.SpellId == 0x0005E2F5).Template);
			Assert.Equal("Corruptor", summons.Single(s => s.SpellId == 0x0004F813).Template);
			Assert.Single(summons.Where(s => !s.Combat), s => s.Template == "Pleasurable Company");

			// A cast summon has no lifetime - the necro's pets stand until dismissed. Only a TIMED summon carries one.
			Assert.All(summons, s => Assert.Null(s.DurationMs));
		}

		// Frozen Hatred's proc, in the client's own words (spell 4377209's description, LDB 50211): "When you or your
		// group perform a fatality and this buff is active, the Frozen Hatred pet appears and is under your control for
		// 60 seconds. If you already have a Frozen Hatred pet, the duration of the pet will increase by another 60
		// seconds. This timer is capped at 5 minutes." Both halves hang off the BUFF (4377209 - the effect record the
		// castable 4377207 points at, #434), not off the castable, and both fire on the fatality trigger.
		[Fact]
		public void Seed_ArmsFrozenHatredOnAFatality()
		{
			var procs = SpellEffectsModule.BuildRows().Where(r => r.Trigger == SpellEffectTrigger.OnFatality).OrderBy(r => r.Sequence).ToList();

			Assert.Equal(2, procs.Count);
			Assert.All(procs, p => Assert.Equal(4377209u, p.SpellId));

			// The 6s self-buff rides the burst's own frame first (capture fr1267), then the pet.
			Assert.Equal(SpellEffectKind.ApplySpell, procs[0].Kind);
			Assert.Equal(3666099u, procs[0].TargetSpellId);

			Assert.Equal(SpellEffectKind.SummonPet, procs[1].Kind);
			Assert.Equal("Frozen Hatred", procs[1].Template);
			Assert.Equal(60_000u, procs[1].DurationMs);
			Assert.Equal(300_000u, procs[1].MaxDurationMs);
		}

		// The shipped rows ARE the seed. Without this the module and faolan.sql can drift silently: the server reads
		// the database, so a seed nobody re-ran is a set of effects that never fire.
		[Fact]
		public async Task ShippedRows_MatchTheSeed()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var shipped = await db.SpellEffects.AsNoTracking().OrderBy(e => e.SpellId).ThenBy(e => e.Trigger).ThenBy(e => e.Sequence).ToListAsync(ct);
			var seed = SpellEffectsModule.BuildRows().OrderBy(e => e.SpellId).ThenBy(e => e.Trigger).ThenBy(e => e.Sequence).ToList();

			Assert.Equal(seed.Select(Key), shipped.Select(Key));
			return;

			static string Key(Core.Database.Tables.SpellEffect e)
			{
				return $"{e.SpellId}/{e.Trigger}#{e.Sequence} {e.Kind} target={e.TargetSpellId} template={e.Template} combat={e.Combat} dur={e.DurationMs} cap={e.MaxDurationMs}";
			}
		}

		// SpellEffect.SpellId is an FK to Spells, so every effect's spell must be cataloged - and a TargetSpellId the
		// catalog lacks would be applied blind, with no name and no duration. The module refuses to seed either; this
		// proves the shipped database holds them.
		[Fact]
		public async Task EveryEffectSpell_IsCataloged()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var ids = await db.SpellEffects.AsNoTracking().Select(e => e.SpellId).Distinct().ToListAsync(ct);
			var cataloged = await db.Spells.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);

			Assert.Empty(ids.Except(cataloged));
		}
	}
}
