using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #784: WALKING the `CastNano` chain. #783 imported the edge - "when this spell lands, also run X on the same
	// subject" - and deliberately stopped there, so `Gangrenous Stench`'s 221069 reached `Physical Wrack` 3963058 in
	// the database and nowhere else. The data was reachable; the mechanic was not.
	//
	// These drive the MOB half (`WorldSimulation.ApplyEffectToEntity`, the subject an area's effect lands on) because
	// that is where the headline case lives. The player half is the same walk in `GameServerListener.ApplySpell`.
	public class SpellChainWalkTests
	{
		private const uint MapId = 4010; // Tortage Beach
		private const uint PlayerHandle = 0x01316e68;
		private const uint MobHandle = 0x01400001;
		private const uint Stench = 221069; // Gangrenous Stench's effect record - the carrier of the chain edge
		private const uint Wrack = 3963058; // Physical Wrack - the -10% Slashing/Piercing/Crushing/Poison debuff
		private const uint CastNanoKind = 53051;

		// The headline case end to end: the effect lands on a mob AND the spell its chain edge names lands on the SAME
		// mob. Both carriers go out, both are tracked, and the chain edge is the SHIPPED row rather than a fabricated
		// one - fed from the database so a re-harvest that drops it fails here.
		[Fact]
		public async Task AnEffectLandingOnAMob_AlsoLandsTheSpellItsChainEdgeNames()
		{
			var (sim, output, areas, client, mob) = await Harness();
			areas.Chains[Stench] = await ShippedChainEdges(Stench);
			output.AreaVictims = [MobHandle];

			await sim.ApplyAreaEffect(client, PlayerHandle, PlayerHandle, Area(Stench));

			Assert.Equal([Stench << 8, Wrack << 8], output.Applications.Select(a => a.WireSpellId));
			Assert.All(output.Applications, a =>
			{
				Assert.Equal(MobHandle, a.ApplyTo); // the SAME subject - a chain edge states no area of its own
				Assert.Equal(PlayerHandle, a.ApplyFrom);
			});

			// Tracked, so the world tick counts both down and sends both end twins. Physical Wrack is 30 s where its
			// carrier is 9.5 s, which is the whole reason it needs its own entry rather than riding the carrier's.
			Assert.Equal([Stench << 8, Wrack << 8], mob.ActiveBuffs.Keys.Order());
			Assert.Equal(3000u, output.Applications.Single(a => a.WireSpellId == Wrack << 8).DurationCentiseconds);
		}

		// The re-apply guard covers the chain too. An aura re-emits its area every period, and a victim already
		// carrying the effect is not a fresh landing - re-running the edge there would re-assert the chained debuff on
		// every tick, which is the icon-stacking failure of #274 one record further down.
		[Fact]
		public async Task AVictimAlreadyCarryingTheEffect_DoesNotReRunTheChain()
		{
			var (sim, output, areas, client, mob) = await Harness();
			areas.Chains[Stench] = await ShippedChainEdges(Stench);
			output.AreaVictims = [MobHandle];

			await sim.ApplyAreaEffect(client, PlayerHandle, PlayerHandle, Area(Stench));
			await sim.ApplyAreaEffect(client, PlayerHandle, PlayerHandle, Area(Stench));

			Assert.Equal(2, output.Applications.Count); // the carrier and its chained debuff, ONCE each
			Assert.Equal([Stench << 8, Wrack << 8], mob.ActiveBuffs.Keys.Order());
		}

		// ⚠️ The guard is not defensive tidiness - the shipped catalog contains 56 cycles, so an unguarded walk does
		// not terminate on real data. A back-edge applies each spell once and stops.
		[Fact]
		public async Task ACycleInTheChain_TerminatesWithEachSpellAppliedOnce()
		{
			var (sim, output, areas, client, mob) = await Harness();
			areas.Chains[Stench] = [Link(Stench, Wrack)];
			areas.Chains[Wrack] = [Link(Wrack, Stench)]; // ...and back again
			output.AreaVictims = [MobHandle];

			await sim.ApplyAreaEffect(client, PlayerHandle, PlayerHandle, Area(Stench));

			Assert.Equal([Stench << 8, Wrack << 8], output.Applications.Select(a => a.WireSpellId));
			Assert.Equal(2, mob.ActiveBuffs.Count);
		}

		// The fan-out bound, on the walk itself: a chain wide enough to flood the wire stops at the cap and SAYS it
		// stopped, rather than truncating silently. Closure sizes are a median of 2, but the largest in the catalog is
		// 78 and one landing does not get to emit 78 ApplySpell carriers.
		[Fact]
		public void TheWalk_StopsAtItsApplicationCapAndRecordsThat()
		{
			var walk = new SpellChainWalk();

			var admitted = Enumerable.Range(1, SpellChainWalk.MaxApplications + 10).Count(i => walk.TryEnter((uint)i));

			Assert.Equal(SpellChainWalk.MaxApplications, admitted);
			Assert.True(walk.Truncated);
		}

		// ...and a spell already applied on this walk is refused without spending the cap, which is what makes a
		// DIAMOND (two carriers chaining the same debuff) cost one application rather than two.
		[Fact]
		public void TheWalk_RefusesASpellItHasAlreadyApplied()
		{
			var walk = new SpellChainWalk();

			Assert.True(walk.TryEnter(Wrack));
			Assert.False(walk.TryEnter(Wrack));
			Assert.False(walk.Truncated);
		}

		// The chain edges the SHIPPED catalog states for `spellId`, so the gates above walk retail's own data.
		private static async Task<IReadOnlyList<SpellAreaEffect>> ShippedChainEdges(uint spellId)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.SpellAreaEffects.AsNoTracking()
				.Where(a => a.SpellId == spellId && a.Kind == CastNanoKind)
				.OrderBy(a => a.Sequence)
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(rows);
			return rows;
		}

		private static SpellAreaEffect Link(uint spellId, uint effectSpellId)
		{
			return new SpellAreaEffect { SpellId = spellId, Kind = CastNanoKind, EffectSpellId = effectSpellId };
		}

		private static SpellAreaEffect Area(uint effectSpellId)
		{
			return new SpellAreaEffect { SpellId = 221070, Sequence = 0, Kind = 53088, EffectSpellId = effectSpellId, RadiusMetres = 7, MaxTargets = 12 };
		}

		private static async Task<(WorldSimulation Sim, RecordingWorldOutbound Output, RecordingSpellAreaCatalog Areas, RecordingNetworkClient Client, WorldEntity Mob)> Harness()
		{
			var output = new RecordingWorldOutbound();
			var areas = new RecordingSpellAreaCatalog();
			var sim = new WorldSimulation(output, new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase))), null!, NullLogger.Instance, areas);

			var client = new RecordingNetworkClient(PlayerHandle);
			var player = new WorldEntity(PlayerHandle, MapId, true, "Caster", client);
			sim.Players[client] = player;
			sim.World.Place(player, MapId, Vector3.Zero, Quaternion.Identity);

			var mob = new WorldEntity(MobHandle, MapId, false, "Rotted Corpse", null!);
			sim.World.Place(mob, MapId, Vector3.Zero, Quaternion.Identity);

			return await Task.FromResult((sim, output, areas, client, mob));
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
