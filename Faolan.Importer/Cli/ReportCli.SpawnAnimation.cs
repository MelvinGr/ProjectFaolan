using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
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
	// ⚠️ HOW LONG the window is is bounded ABOVE at **2.6 seconds** by the tightest observation, and BELOW at 25.6s by
	// six entities flagged on two sightings - which cannot both be one window. The lower bound is weak (five of the six
	// are one co-streaming group in one capture, i.e. one event), so it does not settle anything on its own.
	//
	// ## ⭐⭐ AND THE CLIENT'S CONSUMER SETTLES THE READING - it IS the entrance animation (#800)
	//
	// The wire field lands on client stat `0x19C SpawnAnimation` (`FUN_01458d40` copies `npc_stats+0x24` into it,
	// which is field 8's slot under the five field->stat identities #797 pinned), and `SimpleChar_t`'s model build -
	// virtual slot 36, `FUN_01104750` - consumes it:
	//
	//     if (GetStat(0x19C) != 0) {
	//         name = monsterData->spawnanim.empty() ? monsterData->defaultidleanim    // +0x13C
	//                                               : monsterData->spawnanim;         // +0x158
	//         if (ResolveAnim(name, &length)) PlayAnim(..., 0x80, ...);               // channel "Control/Anim"
	//         SetStat(0x19C, 0);                                                      // ONE-SHOT
	//     }
	//
	// See `tools/ghidra/DumpSpawnAnimationConsumer.java`.
	//
	// ## ⛔ WHICH OVERTURNS #794's REFUTATION of the `spawnanim` route
	//
	// #794 proposed driving the sender off each creature's own `spawnanim`, and #607 asked the right question first:
	// *does the flag only ever appear on creatures that declare one?* Measured: **no** - `spawnanim` is a RARE
	// attribute (174 of the 3,185 shipped MonsterData records carry it, on undead, demons, frost giants and wraiths,
	// things that claw out of the ground) while of the templates the corpus flags, **275 wear a creature that declares
	// none and only 14 wear one**. The census below still prints that.
	//
	// ⚠️ But the INFERENCE from it - "so the flag cannot mean play this creature's entrance animation" - does not
	// follow, because the client takes `defaultidleanim` for exactly those creatures, in an explicit branch on whether
	// the `spawnanim` string is empty (`[monsterData+0x168]` is its `_Mysize`). A flagged Citizen replays its idle
	// animation: invisible, not wrong. So a rare `spawnanim` is no evidence about the flag at all.
	//
	// The sender side stays unimplemented, now because the SENDER'S RULE is open (the client clears the stat, so the
	// duration is not client-side and the corpus cannot pin when retail sets it) - tracked in Gitea #803.
	public static partial class ReportCli
	{
		private static void SpawnAnimation(IReadOnlyList<NamedCapture> captures, string? rdbDir)
		{
			var burst = new SpawnAnimationBucket();
			var midSession = new SpawnAnimationBucket();
			var noBoundary = 0;

			// #794: template -> its creature asset, and which templates the corpus ever flags. Crossed against the
			// RDB below to answer "does the flag only appear on creatures that HAVE an entrance animation?".
			var monsterDataByTemplate = new Dictionary<uint, uint>();
			var flaggedTemplates = new HashSet<uint>();

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

					// ⚠️ #803: keying this by `p.Port` as well - to test "each SESSION is told once", which would
					// explain the flagged-twice entities below as one delivery seen by two viewers - is a NO-OP, and
					// the reason is worth writing down. `CapturedPacket.Port` is the GAME SIDE's port (the service
					// tier), so both clients of a two-client dump share it and both sides of a playfield transition
					// differ. Re-keyed, the counts come back identical: 14,777 entities, 351 mixed, the same six
					// anomalies. So the corpus cannot separate two RECIPIENTS at all, and any reading that turns on
					// "who was told" is untestable here rather than untested.
					var key = (c.Name, spawn.Handle, npc.TemplateId);
					if (!perEntity.TryGetValue(key, out var sightings))
						perEntity[key] = sightings = [];

					sightings.Add((present, p.Ticks));

					// #794's own precondition, collected from the SAME spawn body so it needs no DB: which creature
					// asset each flagged template wears. `monster_data` is the RDB 1010201 record whose `spawnanim`
					// attribute names the entrance animation.
					if (spawn.Character.Stats?.MonsterData is > 0 and var md)
					{
						monsterDataByTemplate[npc.TemplateId] = (uint)md;
						if (present)
							flaggedTemplates.Add(npc.TemplateId);
					}
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
			SpawnAnimAssetCheck(rdbDir, monsterDataByTemplate, flaggedTemplates);
		}

		// ⭐ #794's precondition, and it FAILS. The plan was to drive the flag off each creature's own `spawnanim`
		// asset - a MonsterData (RDB 1010201) attribute - and #607 asked the right question first: does the flag only
		// ever appear on creatures that declare one?
		//
		// It does not. `spawnanim` is a rare attribute (174 of the 3,184 shipped MonsterData records carry it, on
		// undead, demons, frost giants and wraiths - things that claw out of the ground), while the flag lands on
		// Citizens, Guards and Noblemen. So the flag cannot mean "play this creature's entrance animation": most of
		// the creatures it is sent for have no entrance animation to play.
		//
		// Skipped, with a line saying so, when no RDB is configured - the rest of the report needs only captures.
		private static void SpawnAnimAssetCheck(string? rdbDir, Dictionary<uint, uint> monsterDataByTemplate, HashSet<uint> flaggedTemplates)
		{
			Console.WriteLine();
			Console.WriteLine("⭐ Does the flag only appear on creatures that HAVE an entrance animation? (#794)");

			if (string.IsNullOrEmpty(rdbDir))
			{
				Console.WriteLine("  (skipped - no Content:RdbDir configured, and this half reads the client's MonsterData records.)");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var declares = new Dictionary<uint, string?>();
			var shipped = 0;
			var withAnim = 0;
			foreach (var e in rdb.EntriesOfType(MonsterDataFile.Type).Where(rdb.CanRead))
			{
				string? anim;
				try
				{
					anim = SpawnAnimOf(rdb.ReadDecompressed(e));
				}
				catch
				{
					continue; // not a readable record (CDN-only / not zlib)
				}

				shipped++;
				if (anim != null)
					withAnim++;

				declares[e.Id] = anim;
			}

			Console.WriteLine($"  {withAnim:N0} of the {shipped:N0} shipped MonsterData records declare a `spawnanim` at all.");

			var flaggedWith = 0;
			var flaggedWithout = 0;
			var examples = new List<uint>();
			foreach (var t in flaggedTemplates)
			{
				if (!monsterDataByTemplate.TryGetValue(t, out var md) || !declares.TryGetValue(md, out var anim))
					continue;
				if (anim != null)
				{
					flaggedWith++;
					continue;
				}

				flaggedWithout++;
				if (examples.Count < 8)
					examples.Add(t);
			}

			Console.WriteLine($"  of the templates the corpus FLAGS: {flaggedWith} wear a creature that declares one, {flaggedWithout} do not.");
			if (flaggedWithout == 0)
				Console.WriteLine("  ⭐ The flag is confined to creatures with an entrance animation - a second, independent confirmation.");
			else
				Console.WriteLine($"  ⛔ The flag is NOT confined to them (e.g. templates {string.Join(", ", examples)}), so it cannot mean\n"
					+ "     \"play this creature's entrance animation\" - most of the creatures it is sent for have none (#794).");
		}

		// The root `<monsterdata spawnanim="...">` attribute, or null when the record does not declare one (or is one
		// of the handful of 1010201 entries that hold a different document entirely).
		private static string? SpawnAnimOf(byte[] decompressed)
		{
			var xml = RdbXml.Carve(Encoding.Latin1.GetString(decompressed), "monsterdata");
			if (xml == null)
				return null;

			var m = SpawnAnimRx.Match(xml);

			return m.Success
				? m.Groups[1].Value
				: null;
		}

		private static readonly Regex SpawnAnimRx = new Regex("<monsterdata\\b[^>]*\\bspawnanim=\"([^\"]*)\"", RegexOptions.Compiled);

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

			LowerBound(repeats);
		}

		// ⭐ The other end of the bracket (#794). An entity flagged on TWO sightings was still inside its window at the
		// later one, so the gap between them is a LOWER bound - the one number the upper-bound distribution above
		// cannot give, and the reason the window had only ever been bounded from one side.
		//
		// ⚠️ **Only entities with NO unflagged sighting in between count.** Retail RECYCLES a handle, so
		// flagged -> unflagged -> flagged under one (capture, handle, template) key is two different entities, and its
		// second window is a fresh one rather than the first still running. Including those would manufacture a lower
		// bound out of a respawn. The report prints how many were dropped for exactly that reason.
		private static void LowerBound(List<KeyValuePair<(string Capture, uint Handle, uint Template), List<(bool Flag, long Ticks)>>> repeats)
		{
			var multiFlag = repeats.Where(kv => kv.Value.Count(v => v.Flag) > 1).ToList();
			var contiguous = multiFlag
				.Where(kv =>
				{
					var first = kv.Value.FindIndex(v => v.Flag);
					var last = kv.Value.FindLastIndex(v => v.Flag);

					return kv.Value.Skip(first).Take(last - first + 1).All(v => v.Flag);
				})
				.ToList();

			var recycled = multiFlag.Count - contiguous.Count;
			var spans = contiguous
				.Select(kv => kv.Value.Where(v => v.Flag).ToList())
				.Select(f => (f[^1].Ticks - f[0].Ticks) / (double)TimeSpan.TicksPerSecond)
				.Where(g => g > 0)
				.Order()
				.ToList();

			if (recycled > 0)
				Console.WriteLine($"\n  ({recycled} entit(ies) flagged twice with an UNFLAGGED sighting in between - a recycled handle, not one window - excluded.)");

			Console.WriteLine();
			if (spans.Count == 0)
			{
				Console.WriteLine("  the window, bounded BELOW: no entity is flagged on two sightings, so there is no lower bound at all.");
				return;
			}

			Console.WriteLine($"  the window, bounded BELOW by the gap between two FLAGGED sightings of one entity ({spans.Count} entit(ies)):");
			Console.WriteLine($"    the largest such gap is {spans[^1]:F1}s - the window is at least that long.");

			// Named, because a lower bound resting on six entities has to be checkable by hand: which capture, which
			// handle, and how many sightings apart.
			foreach (var kv in contiguous.OrderByDescending(kv => kv.Value[^1].Ticks - kv.Value[0].Ticks).Take(6))
			{
				var flagged = kv.Value.Where(v => v.Flag).ToList();
				Console.WriteLine($"      {(kv.Key.Capture.Length > 46 ? kv.Key.Capture[..46] : kv.Key.Capture),-46} handle 0x{kv.Key.Handle:X8} template {kv.Key.Template,-6} "
					+ $"{flagged.Count} flagged of {kv.Value.Count} sighting(s), spanning {(flagged[^1].Ticks - flagged[0].Ticks) / (double)TimeSpan.TicksPerSecond:F1}s");
			}

			// The two bounds, CROSSED per TEMPLATE: the smallest upper bound against the largest lower bound. If a
			// creature's own window were one number - which is what "the length of its `spawnanim` asset" would mean -
			// then for every template max-lower <= min-upper, and a template that breaks it has no single window.
			//
			// ⚠️ Suggestive, not conclusive, and the report says so where it prints: the lower bounds it draws on are
			// five co-streaming entities in ONE capture, which is one event rather than five observations.
			var perTemplate = new Dictionary<uint, (double Upper, double Lower)>();
			foreach (var kv in repeats)
			{
				var t = kv.Key.Template;
				perTemplate.TryGetValue(t, out var cur);
				var upper = cur.Upper == 0 ? double.MaxValue : cur.Upper;
				var lower = cur.Lower;

				if (kv.Value[0].Flag && kv.Value.FirstOrDefault(v => !v.Flag) is { Flag: false } off)
					upper = Math.Min(upper, (off.Ticks - kv.Value[0].Ticks) / (double)TimeSpan.TicksPerSecond);

				// Same contiguity rule as LowerBound: a flagged -> unflagged -> flagged run is a recycled handle, and
				// its span is two windows rather than one.
				var firstFlag = kv.Value.FindIndex(v => v.Flag);
				var lastFlag = kv.Value.FindLastIndex(v => v.Flag);
				if (lastFlag > firstFlag && kv.Value.Skip(firstFlag).Take(lastFlag - firstFlag + 1).All(v => v.Flag))
					lower = Math.Max(lower, (kv.Value[lastFlag].Ticks - kv.Value[firstFlag].Ticks) / (double)TimeSpan.TicksPerSecond);

				perTemplate[t] = (upper == double.MaxValue ? 0 : upper, lower);
			}

			var bracketed = perTemplate.Where(kv => kv.Value.Upper > 0 && kv.Value.Lower > 0).ToList();
			var broken = bracketed.Where(kv => kv.Value.Lower > kv.Value.Upper).ToList();

			Console.WriteLine();
			Console.WriteLine($"  ⭐ CROSSING the two, per TEMPLATE ({bracketed.Count} template(s) have both a lower and an upper bound):");
			Console.WriteLine($"    {broken.Count} of them have max-lower > min-upper, which no single per-creature window can satisfy.");
			foreach (var kv in broken.OrderByDescending(kv => kv.Value.Lower - kv.Value.Upper).Take(8))
				Console.WriteLine($"      template {kv.Key,-8} at least {kv.Value.Lower,6:F1}s and at most {kv.Value.Upper,6:F1}s");
			if (broken.Count > 0)
			{
				Console.WriteLine("    ⚠️ Read the lower-bound lines above before leaning on this: if they are one co-streaming GROUP in");
				Console.WriteLine("       one capture, the crossing rests on ONE event, and a simultaneous respawn of that group would");
				Console.WriteLine("       explain it without any window being longer than the upper bounds. The MonsterData check below");
				Console.WriteLine("       is the load-bearing one - it needs no timing at all.");
			}
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
