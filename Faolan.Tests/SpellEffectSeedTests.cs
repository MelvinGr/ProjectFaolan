using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Modules;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The SpellEffect seed (#439): the decoded facts that used to be hardcoded spell ids in the server - the necro's
	// pet summons and Frozen Hatred's fatality proc - now live as rows, so these gate the rows rather than constants.
	public class SpellEffectSeedTests
	{
		// The summon roster is DERIVED from the catalog now (#640), so the gate is the derivation, not a list. The
		// twelve pets one necro capture showed - NecroL80_..._mount-horse_all-necro-summons_dismiss-all, every one
		// summoned and mass-dismissed - are exactly what the old hand-typed table held, so they are the regression
		// floor: whatever else the rule finds, it must still find these.
		[Fact]
		public async Task Summons_StillCarryTheTwelveCapturedPets()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var summons = await db.SpellEffects.AsNoTracking()
				.Where(e => e.Trigger == SpellEffectTrigger.OnCast && e.Kind == SpellEffectKind.SummonPet)
				.ToListAsync(ct);

			Assert.Equal("Arcanist", summons.Single(s => s.SpellId == 0x0005E2F5).Template);
			Assert.Equal("Corruptor", summons.Single(s => s.SpellId == 0x0004F813).Template);
			Assert.Equal("Life-stealer", summons.Single(s => s.SpellId == 0x0004F817).Template);
			Assert.Single(summons.Where(s => !s.Combat), s => s.Template == "Pleasurable Company");

			// A cast summon has no lifetime - the necro's pets stand until dismissed. Only a TIMED summon carries one.
			Assert.All(summons, s => Assert.Null(s.DurationMs));

			// The eleven combat pets of that capture, plus the social companion.
			uint[] captured = [0x004105E9, 0x0005E2F5, 0x0005E2F0, 0x0005E2F6, 0x0005E321, 0x0005E322, 0x0004F813, 0x0004F814, 0x0004F815, 0x0004F816, 0x0004F817, 0x00127EDD];
			Assert.Empty(captured.Except(summons.Select(s => s.SpellId)));
		}

		// The naming rule, stated on retail's own spell names. This is the whole derivation of WHICH creature a summon
		// spawns, so it is worth pinning separately from the rows it produces.
		[Theory]
		[InlineData("Undead Minion: Corruptor", "Corruptor")]
		[InlineData("Deathless Acolyte: Arcanist", "Arcanist")]
		[InlineData("Companion: Pleasurable Company", "Pleasurable Company")]
		[InlineData("Mini-Pet: Serpent Man Oldblood", "Serpent Man Oldblood")]
		[InlineData("Ancient Soul", "Ancient Soul")] // no prefix: the whole name IS the pet
		public void PetNameOf_TakesTheNameAfterThePrefix(string spellName, string expected)
		{
			Assert.Equal(expected, SpellEffectsModule.PetNameOf(spellName));
		}

		// The catalog half: the RDB names far more summons than any capture does, and losing that column would take
		// the roster with it. 319 is what the shipped catalog carries; the gate is that it has not collapsed.
		[Fact]
		public async Task Catalog_NamesTheSummonSpells()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var summonSpells = await db.Spells.AsNoTracking().Where(s => s.SummonSpawnHash != null).ToListAsync(ct);
			Assert.True(summonSpells.Count >= 300, $"only {summonSpells.Count} spells carry summon params - has rdb-spells been run?");

			// Both param spellings are present, and the combat/companion split follows them.
			Assert.Contains(summonSpells, s => !s.SummonIsCompanion && s.SummonSpawnHash == "YU86"); // Deathless Acolyte: Arcanist
			Assert.Contains(summonSpells, s => s.SummonIsCompanion && s.Id == 4261353u); // Companion: Pleasurable Company

			// Spells sharing a pet share a hash - the corroboration that makes a wrong naming rule visible.
			var mutilator = summonSpells.Where(s => s.SummonSpawnHash == "GGDI").ToList();
			Assert.True(mutilator.Count >= 2, "the Mutilator summons should share one spawnHash");
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

			// The derived half is rebuilt from the very catalog the shipped rows were built from, so this asserts the
			// derivation is still the one that produced them - a rule change that silently drops a pet fails here.
			var summonSpells = await db.Spells.AsNoTracking().Where(s => s.SummonSpawnHash != null).ToListAsync(ct);
			var templates = (await db.NpcTemplates.AsNoTracking().Select(t => t.Name).Distinct().ToListAsync(ct))
				.GroupBy(n => n.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

			var seed = SpellEffectsModule.BuildSummonRows(summonSpells, templates).Rows
				.Concat(SpellEffectsModule.BuildRows())
				.OrderBy(e => e.SpellId).ThenBy(e => e.Trigger).ThenBy(e => e.Sequence).ToList();

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
