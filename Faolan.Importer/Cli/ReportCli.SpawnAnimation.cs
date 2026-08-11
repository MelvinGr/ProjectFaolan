using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report spawn-animation`: is `npc_stats.spawn_animation` a property of the spawn EVENT rather than of the
	// entity? (Gitea #607.)
	//
	// #607 established what it is not. Retail never sends it as `0` - over the captured spawns it is either UNSET or
	// `1`, so it is a presence FLAG - and no field of the spawn body predicts which spawns carry it: `--report
	// field-predictors` cannot beat the 91.7% a constant scores, the TYPE itself reaches only 93.6%, and the same
	// creature name appears in both buckets (Black Dragon Guard: 53 with, 234 without).
	//
	// Its own name is the hypothesis: *play the spawn-in animation for this entity right now*. If that is what it means
	// then the discriminator is not IN the body at all - it is WHY the packet was sent. A login burst delivers NPCs that
	// were already standing there; a mid-session spawn is an entity ARRIVING. That is a property of the packet's place
	// in the session, which is exactly the class of predictor a field census cannot see.
	//
	// So this partitions every captured NPC spawn on that axis. The burst boundary is the client's first
	// `UpdateQuestMarkerNPC` poll - the same boundary `NpcSpawnExtractor.ExtractLoginBurst` uses, and it means "the
	// client has taken delivery of the burst and started asking about individual NPCs".
	//
	// ⛔ **The hypothesis is FALSIFIED, and this report is what falsified it.** If the flag meant "this entity is
	// arriving", the burst rate would be ~0 and the mid-session rate high. Measured, it is the other way round:
	//
	//     LOGIN BURST    4,536 spawns,   333 with the flag   7.3%
	//     MID-SESSION   18,580 spawns, 1,076 with the flag   5.8%
	//
	// 333 spawns carry it inside a burst, so it plainly does not mean "arriving". Per creature it is worse than a wash:
	// only 14 of the 351 names in both partitions behave as predicted, and several go the WRONG way outright -
	// `Knight` 8/16 in the burst against 0/156 mid-session, `Priest` 6/26 against 0/132.
	//
	// Kept rather than deleted, because the axis is the right KIND of predictor for a per-spawn-event field (a field
	// census cannot see it at all) and the next hypothesis should be tested the same way. ⚠️ Note the confound if it
	// is: the partition is per capture, so a name concentrated in one session can carry that session's habit rather
	// than a rule - see the `report_predictors_must_be_per_event_not_per_capture` note.
	//
	// ## ⭐⭐ AND THE STICKINESS TEST SETTLED IT: the flag is a DECAYING STATE
	//
	// With "this entity is arriving" dead, two readings survived - a per-creature CAPABILITY, or a transient STATE of
	// one entity. Both aggregate views are blind to the difference; it only shows when ONE entity's own sightings are
	// read in order. Over the 4,687 entities the corpus sights more than once:
	//
	//     always flagged        1
	//     never flagged     4,335
	//     MIXED               351   <- each one refutes CAPABILITY
	//
	//     of the 351 mixed: 346 are ON for the FIRST sighting and off thereafter
	//                         0 turn ON after being off
	//
	// **Zero counterexamples in 4,687 entities.** So the flag marks an entity inside a window that has closed by the
	// next sighting, and the burst result stops being a paradox: a login burst delivers a whole zone at once, and ~7%
	// of a zone's NPCs are inside their respawn window at any moment.
	//
	// ⭐ The CLIENT corroborates the reading independently. `spawnanim` is a real MonsterData (RDB 1010201) XML
	// attribute - `FUN_010005e0` parses it into `MonsterData+0x158` through the same animation-name resolver
	// `defaultidleanim` uses, and `FUN_01db2740` registers it under the property name `SpawnAnimation`. So the wire
	// flag names a concrete per-creature ENTRANCE ANIMATION the client already holds, and "play it" is only meaningful
	// while it is still running.
	//
	// ⚠️ HOW LONG the window is is bounded ABOVE only, and the bound is the useful number: the tightest observation
	// puts it under **2.6 seconds**, which is an animation length rather than a server timer. The sender side stays
	// unimplemented on that basis - `spawnanim` is a per-creature ASSET, so its duration is per creature, and picking
	// one number would be inventing a duration retail does not use. See #607.
	public static partial class ReportCli
	{
		private static void SpawnAnimation(IReadOnlyList<NamedCapture> captures)
		{
			var burst = new SpawnAnimationBucket();
			var midSession = new SpawnAnimationBucket();
			var noBoundary = 0;

			// The STICKINESS test (below): every sighting of one entity within one session, in order. Keyed by
			// (capture, handle, templateId) - the handle alone would merge two creatures, because retail RECYCLES a
			// handle once its entity is gone.
			var perEntity = new Dictionary<(string Capture, uint Handle, uint Template), List<(bool Flag, long Ticks)>>();

			foreach (var c in captures)
			{
				// Where the login burst ends: the first c2s quest-marker poll. A capture with none (a create-flow or a
				// mid-session-only dump) has no boundary, and its spawns are counted apart rather than guessed at.
				var boundary = -1;
				for (var i = 0; i < c.Packets.Count; i++)
				{
					if (c.Packets[i] is { IsDownload: false } p && p.WireOpcode == CapturedPacket.Wire(GameCharAgentMethods.UpdateQuestMarkerNPC))
					{
						boundary = i;
						break;
					}
				}

				for (var i = 0; i < c.Packets.Count; i++)
				{
					var p = c.Packets[i];
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;
					if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) || spawn.Character?.player_stats != null)
						continue;
					if (spawn.Character?.npc_stats is not { } npc)
						continue;

					if (boundary < 0)
					{
						noBoundary++;
						continue;
					}

					var present = npc.ShouldSerializeSpawnAnimation();
					(i < boundary ? burst : midSession).Add(present, spawn.Character.Name ?? "?");

					var key = (c.Name, spawn.Handle, npc.TemplateId);
					if (!perEntity.TryGetValue(key, out var sightings))
						perEntity[key] = sightings = [];

					sightings.Add((present, p.Ticks));
				}
			}

			Console.WriteLine("npc_stats.spawn_animation, partitioned by WHY the spawn was sent (#607)");
			Console.WriteLine("The burst boundary is the client's first UpdateQuestMarkerNPC poll.");
			Console.WriteLine();
			Console.WriteLine("  where                   spawns    with flag   without    with%");
			Console.WriteLine($"  LOGIN BURST          {burst.Total,9}  {burst.With,10}  {burst.Without,8}  {burst.Percent,6:F1}%");
			Console.WriteLine($"  MID-SESSION          {midSession.Total,9}  {midSession.With,10}  {midSession.Without,8}  {midSession.Percent,6:F1}%");
			Console.WriteLine($"  (no burst boundary)  {noBoundary,9}");
			Console.WriteLine();

			// The claim only means something if it holds per CREATURE too: a name that appears in both partitions and
			// carries the flag only in the mid-session one is the same fact seen without the population mixing in.
			var both = burst.Names.Keys.Intersect(midSession.Names.Keys).ToList();
			Console.WriteLine($"{both.Count} creature name(s) appear in BOTH partitions - the within-name check:");
			Console.WriteLine();
			Console.WriteLine("  creature                          burst with/total    mid-session with/total");
			foreach (var name in both.OrderByDescending(n => midSession.Names[n].Total + burst.Names[n].Total).Take(25))
			{
				var b = burst.Names[name];
				var m = midSession.Names[name];
				Console.WriteLine($"  {(name.Length > 30 ? name[..30] : name),-30}  {b.With,7}/{b.Total,-8}       {m.With,7}/{m.Total,-8}");
			}

			var agree = both.Count(n => burst.Names[n].With == 0 && midSession.Names[n].With == midSession.Names[n].Total);
			Console.WriteLine();
			Console.WriteLine($"{agree} of {both.Count} names behave EXACTLY as the hypothesis predicts (never in the burst, always mid-session).");

			StickinessTest(perEntity);
		}

		// ⭐ THE STICKINESS TEST: is the flag a property of the ENTITY, or a transient STATE of it?
		//
		// With the "this entity is arriving" reading dead, the two survivors are:
		//   CAPABILITY - this creature plays a spawn-in animation, others do not. Then a given entity carries the flag
		//                on EVERY sighting, and a re-stream (walking back into interest range) repeats it.
		//   STATE      - the entity is currently INSIDE its spawn-animation window, i.e. it respawned seconds ago.
		//                Then the same entity carries it on some sightings and not others, and the flagged one comes
		//                FIRST - a later re-stream finds the animation finished.
		//
		// Neither the field census nor the burst partition can see this, because both aggregate over entities. It only
        // shows up when one entity's own sightings are read in order.
		//
		// ⚠️ Only entities sighted MORE THAN ONCE can say anything, and a session-scoped handle is the identity - so
		// this is keyed by (capture, handle, templateId) and never by handle alone.
		private static void StickinessTest(Dictionary<(string Capture, uint Handle, uint Template), List<(bool Flag, long Ticks)>> perEntity)
		{
			var repeats = perEntity.Where(kv => kv.Value.Count > 1).ToList();

			Console.WriteLine();
			Console.WriteLine("⭐ THE STICKINESS TEST - a CAPABILITY is carried on every sighting of an entity; a transient");
			Console.WriteLine("   STATE is not. Read per (capture, handle, templateId), which is the only identity a");
			Console.WriteLine("   session-scoped handle supports:");
			Console.WriteLine();
			Console.WriteLine($"  {perEntity.Count:N0} distinct entit(ies), {repeats.Count:N0} of them sighted more than once.");

			if (repeats.Count == 0)
			{
				Console.WriteLine("  ⛔ No entity is sighted twice, so the corpus cannot separate capability from state.");
				return;
			}

			var alwaysOn = repeats.Count(kv => kv.Value.All(v => v.Flag));
			var alwaysOff = repeats.Count(kv => kv.Value.All(v => !v.Flag));
			var mixed = repeats.Count - alwaysOn - alwaysOff;

			Console.WriteLine($"    always flagged      {alwaysOn,7}");
			Console.WriteLine($"    never flagged       {alwaysOff,7}");
			Console.WriteLine($"    MIXED               {mixed,7}   <- each one refutes CAPABILITY");
			Console.WriteLine();

			if (mixed == 0)
			{
				Console.WriteLine("  ⭐ Not one entity ever changes its answer, so the flag is a property of the ENTITY, not a state");
				Console.WriteLine("     of it - and the remaining question is which entities, not when.");
				return;
			}

			// If it is a state that DECAYS, the flagged sighting is the first one. A mixed entity whose flag turns ON
			// later would refute that just as hard as it refutes capability.
			var decaying = repeats.Where(kv => kv.Value.Count > 1 && kv.Value[0].Flag && kv.Value.Skip(1).All(v => !v.Flag)).ToList();
			var firstThenOff = decaying.Count;
			var turnsOn = repeats.Count(kv => !kv.Value[0].Flag && kv.Value.Skip(1).Any(v => v.Flag));

			Console.WriteLine($"  of the {mixed} mixed: {firstThenOff} are ON for the FIRST sighting then off (a decaying state),");
			Console.WriteLine($"                  {turnsOn} turn ON after being off (which a decaying state cannot do).");
			Console.WriteLine();
			Console.WriteLine(firstThenOff > turnsOn
				? "  ⭐ The flag DECAYS: it marks an entity inside a window that has passed by the next sighting."
				: "  ⚠️ It does not decay cleanly either. Both readings are refuted; the field stays unexplained (#607).");

			if (decaying.Count == 0)
				return;

			// ⚠️ How long the window is, bounded ABOVE only. The gap measured is flagged-sighting -> first unflagged
			// one, and a sighting happens when the PLAYER walks back into range - so the true window ends somewhere
			// inside that gap and nothing in the corpus narrows it further. Printed as a distribution rather than a
			// number, because a single figure here would read as a measured duration and get implemented as one.
			var gaps = decaying
				.Select(kv => (kv.Value.First(v => !v.Flag).Ticks - kv.Value[0].Ticks) / (double)TimeSpan.TicksPerSecond)
				.Where(g => g > 0).Order().ToList();

			if (gaps.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"  the window, bounded ABOVE by the gap to the first unflagged sighting ({gaps.Count} entit(ies)):");
			foreach (var (label, value) in (( string, double )[])
			         [("min", gaps[0]), ("p25", gaps[gaps.Count / 4]), ("median", gaps[gaps.Count / 2]),
				         ("p75", gaps[gaps.Count * 3 / 4]), ("max", gaps[^1])])
				Console.WriteLine($"    {label,-7} {value,8:F1}s");

			Console.WriteLine();
			Console.WriteLine("  ⚠️ These are UPPER bounds, not the duration: a sighting happens when the player walks back into");
			Console.WriteLine("     range, so the window closed somewhere inside each gap. Nothing in the corpus narrows it, so the");
			Console.WriteLine("     sender side stays unimplemented rather than shipping an invented duration (#607).");
		}

		private sealed class SpawnAnimationBucket
		{
			public int With;
			public int Without;
			public readonly Dictionary<string, SpawnAnimationBucket> Names = [];

			public int Total => With + Without;
			public double Percent => Total == 0 ? 0 : With * 100.0 / Total;

			public void Add(bool present, string? name = null)
			{
				if (present)
					With++;
				else
					Without++;

				if (name == null)
					return;

				if (!Names.TryGetValue(name, out var byName))
					Names[name] = byName = new SpawnAnimationBucket();

				byName.Add(present);
			}
		}
	}
}
