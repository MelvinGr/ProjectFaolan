using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The world tick for buffs sitting on a non-player entity.
	//
	// The gap these gate: `TickBuffExpiry`, `TickDots` and `TickAuras` all walk `Players`, so a debuff a player put on
	// a mob had nothing to expire it, nothing to bleed it and nothing to emit its own areas. `Gangrenous Stench` is
	// the reported case - the aura ran, dealt its initial damage and then did nothing, because the 9.5s effect record
	// its area names never actually landed on anybody. An applied buff with no tick behind it is also the never-
	// expiring orphan icon the player side already guards against, so the two halves have to ship together.
	public class NpcBuffTickTests
	{
		private const uint MapId = 4010; // Tortage Beach
		private const uint PlayerHandle = 0x01316e68;
		private const uint MobHandle = 0x01400001;
		private const uint EffectSpell = 221069; // Gangrenous Stench's effect record
		private const uint EffectWireId = EffectSpell << 8;

		// A DoT on a mob bleeds it, through the caster's client so the kill lifecycle can pay them.
		[Fact]
		public async Task ADotOnAMob_BleedsItOnTheTick_AttributedToTheCaster()
		{
			var (buffs, _, output, _, _, mob) = Harness();
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow.AddSeconds(-5), 30f, dotDelta: -7, dotIntervalMs: 1000));

			await TickNpcBuffs(buffs);

			var tick = Assert.Single(output.DotTicks);
			Assert.Equal(MobHandle, tick.NpcHandle);
			Assert.Equal(PlayerHandle, tick.CasterHandle);
			Assert.Equal(-7, tick.Delta);
			Assert.Equal(EffectSpell, tick.SpellId); // the base id, not the wire form
		}

		// ...and only on its own cadence: a tick inside the interval is not a second bleed. Without this the 10 Hz
		// world loop would apply a 1s DoT ten times a second.
		[Fact]
		public async Task ADotOnAMob_DoesNotBleedTwiceInsideItsInterval()
		{
			var (buffs, _, output, _, _, mob) = Harness();
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 30f, dotDelta: -7, dotIntervalMs: 2000) with { LastDotTick = DateTimeOffset.UtcNow });

			await TickNpcBuffs(buffs);
			await TickNpcBuffs(buffs);

			Assert.Empty(output.DotTicks);
		}

		// The expiry half: a lapsed effect sends the retail end twin so the client drops the icon, and leaves the
		// registry. This is the orphan-icon failure, one entity over.
		[Fact]
		public async Task AnExpiredEffect_SendsTheEndTwinAndLeavesTheRegistry()
		{
			var (buffs, _, output, _, _, mob) = Harness();
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow.AddSeconds(-20), 9.5f));

			await TickNpcBuffs(buffs);

			var removal = Assert.Single(output.Removals);
			Assert.Equal(MobHandle, removal.ApplyTo); // mirrors the apply: applyTo = the affected entity...
			Assert.Equal(PlayerHandle, removal.ApplyFrom); // ...applyFrom = the caster
			Assert.Equal(EffectWireId, removal.SpellId);
			Assert.Empty(mob.ActiveBuffs);
		}

		// The chain that matters: the effect record states an area of its own, so an infected mob spreads it
		// to its neighbours - centred on the mob, not on the player who started it.
		[Fact]
		public async Task AnInfectedMob_EmitsTheEffectsOwnArea_CentredOnItself()
		{
			var (buffs, _, output, areas, _, mob) = Harness();
			areas.Auras[EffectSpell] = [Area(221068, radius: 4, periodCentiseconds: 300)];
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 9.5f));

			await TickNpcBuffs(buffs);

			var area = Assert.Single(output.Areas);
			Assert.Equal(MobHandle, area.SourceHandle); // the emitter is the mob the debuff sits on
			Assert.Equal(221068u, area.EffectSpellId);
			Assert.Equal(4f, area.Radius);
		}

		// The same emission again inside the period is not a second one - the stamp is per (holder, buff, area).
		[Fact]
		public async Task AnInfectedMob_DoesNotReEmitInsideThePeriod()
		{
			var (buffs, _, output, areas, _, mob) = Harness();
			areas.Auras[EffectSpell] = [Area(221068, radius: 4, periodCentiseconds: 300)];
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 9.5f));

			await TickNpcBuffs(buffs);
			await TickNpcBuffs(buffs);

			Assert.Single(output.Areas);
		}

		// Two mobs carrying the same effect each emit their own area on the same tick. A stamp keyed by (buff, area)
		// alone swallows the second holder's emission as "already ticked this period", stalling the spread at one mob
		// however many it has reached.
		[Fact]
		public async Task TwoMobsCarryingTheSameEffect_BothEmit()
		{
			var (buffs, sim, output, areas, client, mob) = Harness();
			areas.Auras[EffectSpell] = [Area(221068, radius: 4, periodCentiseconds: 300)];

			var second = new WorldEntity(MobHandle + 1, MapId, false, "Rotted Corpse", null!);
			sim.World.Place(second, MapId, Vector3.Zero, Quaternion.Identity);

			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 9.5f));
			Track(buffs, second, Buff(DateTimeOffset.UtcNow, 9.5f));

			await TickNpcBuffs(buffs);

			Assert.Equal(2, output.Areas.Count);
			Assert.Equal([MobHandle, MobHandle + 1], output.Areas.Select(a => a.SourceHandle).Order());
			Assert.NotNull(client);
		}

		// A caster who has gone (logged out, changed playfield) takes their debuffs with them: there is no client left
		// to render an end twin at, and no one a kill it lands could be paid to. Dropped silently rather than left
		// bleeding a mob for a player who is not there.
		[Fact]
		public async Task AnEffectWhoseCasterIsGone_IsDroppedWithoutTicking()
		{
			var (buffs, sim, output, _, client, mob) = Harness();
			sim.Players.TryRemove(client, out _);
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 30f, dotDelta: -7, dotIntervalMs: 1000));

			await TickNpcBuffs(buffs);

			Assert.Empty(output.DotTicks);
			Assert.Empty(output.Removals);
			Assert.Empty(mob.ActiveBuffs);
		}

		// A dead mob's buffs go with it. Its kill lifecycle has already run, so a bleed that kept ticking would pay
		// the XP a second time - and a corpse renders no icon to remove.
		[Fact]
		public async Task ADeadMob_LeavesTheRegistryWithoutTicking()
		{
			var (buffs, _, output, _, _, mob) = Harness();
			mob.Vitals = EntityVitals.FromMax(100, 0, 0);
			mob.Vitals.ApplyHealthDelta(-1000);
			Track(buffs, mob, Buff(DateTimeOffset.UtcNow, 30f, dotDelta: -7, dotIntervalMs: 1000));

			await TickNpcBuffs(buffs);

			Assert.True(mob.IsDead);
			Assert.Empty(output.DotTicks);
			Assert.Empty(mob.ActiveBuffs);
		}

		private static ActiveBuff Buff(DateTimeOffset appliedAt, float durationSeconds, int dotDelta = 0, uint dotIntervalMs = 0)
		{
			return new ActiveBuff(EffectWireId, PlayerHandle, appliedAt, durationSeconds)
			{
				DotDelta = dotDelta,
				DotIntervalMs = dotIntervalMs,
				LastDotTick = appliedAt
			};
		}

		private static SpellAreaEffect Area(uint effectSpellId, uint radius, uint periodCentiseconds)
		{
			return new SpellAreaEffect
			{
				SpellId = EffectSpell,
				Sequence = 0,
				EffectSpellId = effectSpellId,
				RadiusMetres = radius,
				MaxTargets = 12,
				PeriodCentiseconds = periodCentiseconds
			};
		}

		private static void Track(BuffTickService buffs, WorldEntity mob, ActiveBuff buff)
		{
			mob.ActiveBuffs[buff.BuffId] = buff;
			buffs.TrackBuffedEntity(mob);
		}

		// The mob's zone ticks its own buffed entities: every entity here sits in MapId's default instance.
		private static Task TickNpcBuffs(BuffTickService buffs)
		{
			return buffs.TickNpcBuffs(new Zone(MapId));
		}

		private static (BuffTickService Buffs, WorldSimulation Sim, RecordingWorldOutbound Output, RecordingSpellAreaCatalog Areas, RecordingNetworkClient Client, WorldEntity Mob) Harness()
		{
			var output = new RecordingWorldOutbound();
			var areas = new RecordingSpellAreaCatalog();
			var parts = TestWorld.Build(spellAreas: areas);
			var sim = parts.Sim;
			sim.BindOutbound(output);

			var client = new RecordingNetworkClient(PlayerHandle);
			var player = new WorldEntity(PlayerHandle, MapId, true, "Caster", client);
			sim.Players[client] = player;
			sim.World.Place(player, MapId, Vector3.Zero, Quaternion.Identity);

			var mob = new WorldEntity(MobHandle, MapId, false, "Rotted Corpse", null!);
			sim.World.Place(mob, MapId, Vector3.Zero, Quaternion.Identity);

			return (parts.BuffTicks, sim, output, areas, client, mob);
		}
	}
}
