using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #766: a standing NPC performs its creature's ambient idle animations, which is what makes a street look inhabited
	// rather than lined with statues. Retail streams them as BehaviorId.Emote records - see
	// BehaviorAnimations.AmbientEmote for the byte-exact record and `--report ambient-animation` for the measurements
	// these gates encode.
	public class AmbientAnimationTests
	{
		[Fact]
		public void AnIdleNpc_PerformsAnAnimation_AtTheMeasuredCadence()
		{
			// The cadence is the animation's own length plus the pause the creature stands for, so a 2s animation plus a
			// ~2.2s median pause is roughly one every 4s. Over 300s that is dozens, not one and not one per tick.
			var (world, npc) = Idler([new AmbientAnimation(62171, 200, 10)]);

			var performed = Count(world, 300.0);

			Assert.InRange(performed, 20, 120);
			Assert.NotNull(npc.AmbientAnimationPicked);
			Assert.Equal(62171u, npc.AmbientAnimationPicked!.Value.AnimationId);
		}

		[Fact]
		public void ACreatureWithNoCapturedAnimations_StandsStill()
		{
			// The honest gap. 222 of the shipped creature types were captured idling; the rest get NOTHING rather than a
			// borrowed animation, because an animation belonging to another creature may not even exist on its skeleton.
			var (world, npc) = Idler([]);

			Assert.Equal(0, Count(world, 300.0));
			Assert.Null(npc.AmbientAnimationPicked);
		}

		[Fact]
		public void ARouteWalker_PerformsOneOnlyWhileItSTANDSAtTheEndOfItsBeat()
		{
			// Retail's own split, not a guess about what looks right: 24,432 of the 25,750 attributable captured Emote
			// records land on an actor with no MoveTask in the preceding 10s.
			//
			// ⚠️ A route walk is MODEL-ONLY - it does not stream, so it never sets `Moving`. Gating on that flag alone
			// would have every patroller in Old Tarantia stopping to scratch itself while walking down the street, which
			// is why this gate walks a real beat rather than setting a flag.
			var beat = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 200) };
			var (world, npc) = Idler([new AmbientAnimation(62171, 200, 10)]);
			npc.PatrolWaypoints = beat;
			npc.WalksRoute = true;
			npc.RejoinRouteAtNearest();

			// 200u at RouteWalkSpeed is 90s of walking against a 3s dwell at the far end, so a walker that ignored the
			// walk would rack up dozens over this window.
			var performed = Count(world, 100.0);

			Assert.InRange(performed, 0, 2);
		}

		[Fact]
		public void AnNpcWithAPlayerInAggroRange_DoesNotPerformOne()
		{
			// A mob about to swing is not idling. Checked through the same proximity question the aggro decision asks,
			// so the two cannot disagree about whether this tick is a fight.
			var (world, npc) = Idler([new AmbientAnimation(62171, 200, 10)]);
			npc.Hostile = true;
			var player = new WorldEntity(99, 2000, true, "P", null!) { Position = new Vector3(2, 0, 0) };
			world.ZoneFor(2000).Add(player);

			Assert.Equal(0, Count(world, 300.0));
		}

		[Fact]
		public void TheNextAnimation_NeverStartsBeforeTheLastHasFINISHED()
		{
			// The measurement that pins the cadence: over 22,696 consecutive captured pairs, the gap divided by the
			// previous record's activation_time has p10 exactly 1.0. A long animation therefore holds the slot for its
			// whole length - a 30s one cannot come round twice in 40s.
			var (world, _) = Idler([new AmbientAnimation(62171, 3000, 10)]); // 30s

			Assert.InRange(Count(world, 40.0), 1, 2);
		}

		[Fact]
		public void ThePick_IsWEIGHTEDByHowOftenRetailPlayedIt()
		{
			// A creature's common idle must stay common: Citizen 14522 plays 62171 about 570 times against 182 for
			// 62188. A pick that ignored the weight would flatten that to 50/50, and - worse - a broken weighted draw
			// that always returns the first entry looks identical to a working one unless the ratio is checked.
			var animations = new[]
			{
				new AmbientAnimation(1, 100, 90),
				new AmbientAnimation(2, 100, 10)
			};
			var random = new Random(1234);

			var counts = new Dictionary<uint, int>();
			for (var i = 0; i < 10000; i++)
			{
				var picked = AmbientAnimations.Pick(animations, random)!.Value;
				counts[picked.AnimationId] = counts.GetValueOrDefault(picked.AnimationId) + 1;
			}

			Assert.InRange(counts[1] / 10000.0, 0.85, 0.95);
			Assert.InRange(counts[2] / 10000.0, 0.05, 0.15);
		}

		[Fact]
		public void EveryShippedAnimation_StatesADurationAndBelongsToAKnownCreature()
		{
			// Gates the harvested rows in faolan.db. A zero duration would tell the client an animation of no length AND
			// schedule the next one immediately, so the creature would flicker between animations forever - the failure
			// the importer's "no stated activation_time" skip exists to prevent, checked here on what actually shipped.
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));
			var rows = ctx.NpcAmbientAnimations.AsNoTracking().ToList();
			var templates = ctx.NpcTemplates.AsNoTracking().Select(t => t.TemplateId).ToHashSet();

			Assert.NotEmpty(rows);
			Assert.All(rows, r =>
			{
				Assert.True(r.DurationCentiseconds > 0, $"template {r.NpcTemplateId} animation {r.AnimationId} states no duration");
				Assert.True(r.AnimationId > 0, $"template {r.NpcTemplateId} has a zero animation id");
				Assert.True(r.Weight > 0, $"template {r.NpcTemplateId} animation {r.AnimationId} was never actually seen");
				Assert.Contains(r.NpcTemplateId, templates);
			});

			// And the durations sit in the range the corpus states - 0.1s to 39s in centiseconds - so a mis-scaled
			// harvest (reading the field as milliseconds, say) fails here rather than making every citizen twitch.
			Assert.All(rows, r => Assert.InRange(r.DurationCentiseconds, 10u, 4000u));
		}

		// Ticks `seconds` of world at 10 Hz and counts the ambient-animation effects.
		private static int Count(WorldState world, double seconds)
		{
			var performed = 0;
			for (var i = 0; i < (int)(seconds * 10); i++)
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type == WorldTickEffectType.NpcAmbientAnimation)
						performed++;
				}
			}

			return performed;
		}

		// A live, non-hostile, standing NPC carrying `animations` - what WorldBroadcaster wires up at zone load.
		private static (WorldState World, WorldEntity Npc) Idler(AmbientAnimation[] animations)
		{
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(7, 2000, Vector3.Zero, Quaternion.Identity, "Citizen", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = false;
			npc.AmbientAnimations = animations;
			world.Place(npc, 2000, Vector3.Zero, Quaternion.Identity);
			return (world, npc);
		}
	}
}
