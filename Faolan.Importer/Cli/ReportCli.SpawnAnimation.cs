using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report spawn-animation`: is `npc_stats.spawn_animation` a property of the spawn event rather than of the
	// entity?
	//
	// Retail never sends the field as `0` - over the captured spawns it is either unset or `1`, so it is a presence
	// flag - and no field of the spawn body predicts which spawns carry it: `--report field-predictors` cannot beat
	// the 91.7% a constant scores, the type itself reaches only 93.6%, and the same creature name appears in both
	// buckets (Black Dragon Guard: 53 with, 234 without).
	//
	// The field's own name is the hypothesis: play the spawn-in animation for this entity right now. If that is what
	// it means then the discriminator is not in the body at all - it is why the packet was sent. A login burst
	// delivers NPCs that were already standing there; a mid-session spawn is an entity arriving. That is a property
	// of the packet's place in the session, which is exactly the class of predictor a field census cannot see.
	//
	// So this partitions every captured NPC spawn on that axis. The burst boundary is the client's first
	// `UpdateQuestMarkerNPC` poll - the same boundary `NpcSpawnExtractor.ExtractLoginBurst` uses, and it means "the
	// client has taken delivery of the burst and started asking about individual NPCs".
	//
	// The arrival reading is falsified by that partition. If the flag meant "this entity is arriving", the burst rate
	// would be ~0 and the mid-session rate high. Measured, it is the other way round:
	//
	//     login burst    4,536 spawns,   333 with the flag   7.3%
	//     mid-session   18,580 spawns, 1,076 with the flag   5.8%
	//
	// Per creature it is worse than a wash: only 14 of the 351 names in both partitions behave as predicted, and
	// several go the wrong way outright - `Knight` 8/16 in the burst against 0/156 mid-session, `Priest` 6/26 against
	// 0/132.
	//
	// The partition is kept because it is the right kind of predictor for a per-spawn-event field (a field census
	// cannot see it at all) and the next hypothesis should be tested the same way. Note the confound: the partition
	// is per capture, so a name concentrated in one session can carry that session's habit rather than a rule.
	//
	// ## The stickiness test: the flag is a decaying state
	//
	// With "this entity is arriving" dead, two readings survive - a per-creature capability, or a transient state of
	// one entity. Both aggregate views are blind to the difference; it only shows when one entity's own sightings are
	// read in order. Over the 4,687 entities the corpus sights more than once:
	//
	//     always flagged        1
	//     never flagged     4,335
	//     mixed               351   <- each one refutes the capability reading
	//
	//     of the 351 mixed: 346 are on for the first sighting and off thereafter
	//                         0 turn on after being off
	//
	// Zero counterexamples. So the flag marks an entity inside a window that has closed by the next sighting, and the
	// burst result stops being a paradox: a login burst delivers a whole zone at once, and ~7% of a zone's NPCs are
	// inside their respawn window at any moment.
	//
	// The client corroborates the reading independently. `spawnanim` is a real MonsterData (RDB 1010201) XML
	// attribute - `FUN_010005e0` parses it into `MonsterData+0x158` through the same animation-name resolver
	// `defaultidleanim` uses, and `FUN_01db2740` registers it under the property name `SpawnAnimation`. So the wire
	// flag names a concrete per-creature entrance animation the client already holds, and "play it" is only meaningful
	// while it is still running.
	//
	// The window length is bounded above at 2.6 seconds by the tightest observation, and below at 25.6s by six
	// entities flagged on two sightings - which cannot both be one window. The lower bound is weak (five of the six
	// are one co-streaming group in one capture, i.e. one event), so it does not settle anything on its own.
	//
	// ## The client's consumer: it is the entrance animation
	//
	// The wire field lands on client stat `0x19C SpawnAnimation` (`FUN_01458d40` copies `npc_stats+0x24` into it,
	// which is field 8's slot), and `SimpleChar_t`'s model build - virtual slot 36, `FUN_01104750` - consumes it:
	//
	//     if (GetStat(0x19C) != 0) {
	//         name = monsterData->spawnanim.empty() ? monsterData->defaultidleanim    // +0x13C
	//                                               : monsterData->spawnanim;         // +0x158
	//         if (ResolveAnim(name, &length)) PlayAnim(..., 0x80,...);               // channel "Control/Anim"
	//         SetStat(0x19C, 0);                                                      // one-shot
	//     }
	//
	// See `tools/ghidra/DumpSpawnAnimationConsumer.java`.
	//
	// ## A rare `spawnanim` is no evidence about the flag
	//
	// `spawnanim` is a rare attribute (174 of the 3,185 shipped MonsterData records carry it, on undead, demons,
	// frost giants and wraiths - things that claw out of the ground) while of the templates the corpus flags, 275
	// wear a creature that declares none and only 14 wear one. The census below prints that.
	//
	// That does not mean the flag cannot be "play this creature's entrance animation": the client takes
	// `defaultidleanim` for exactly those creatures, in an explicit branch on whether the `spawnanim` string is empty
	// (`[monsterData+0x168]` is its `_Mysize`). A flagged Citizen replays its idle animation: invisible, not wrong.
	//
	// ## The respawn test: the sender's set is (re)creation
	//
	// See `RespawnTest` below for the measurement and its control. In one line: 20 of 35 matched respawns carry the
	// flag against 8.1 expected from those same templates' other first-sightings, and the solitary animals - where a
	// positional match cannot pick up a pack-mate - go 6/6 against a combined control of 1/298.
	//
	// So the set is measured and `WorldBroadcaster.BroadcastNpcRespawn` stamps the flag onto the respawn delivery.
	// When retail clears it is unmeasurable here: the gap this report prints is death -> delivery, with an unknown
	// respawn timer inside it, so no flagged delivery bounds the window from below.
	public static partial class ReportCli
	{
		private static void SpawnAnimation(IReadOnlyList<NamedCapture> captures, string? rdbDir)
		{
			var burst = new SpawnAnimationBucket();
			var midSession = new SpawnAnimationBucket();
			var noBoundary = 0;

			// template -> its creature asset, and which templates the corpus ever flags. Crossed against the
			// RDB below to answer "does the flag only appear on creatures that have an entrance animation?".
			var monsterDataByTemplate = new Dictionary<uint, uint>();
			var flaggedTemplates = new HashSet<uint>();

			// The RESPAWN test: every sighting with its position, and every captured death, so the two can be
			// interleaved. A flat sighting list rather than the per-entity one, because a respawned creature does not
			// keep its handle - the match has to be made on (template, position).
			var sightings = new List<SpawnSighting>();
			var deaths = new List<(string Capture, uint Handle, long Ticks)>();

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

					// Keying this by `p.Port` as well - to test "each session is told once", which would explain the
					// flagged-twice entities below as one delivery seen by two viewers - is a no-op, and the reason is
					// worth writing down. `CapturedPacket.Port` is the game side's port (the service tier), so both
					// clients of a two-client dump share it and both sides of a playfield transition
					// differ. Re-keyed, the counts come back identical: 14,777 entities, 351 mixed, the same six
					// anomalies. So the corpus cannot separate two RECIPIENTS at all, and any reading that turns on
					// "who was told" is untestable here rather than untested.
					var key = (c.Name, spawn.Handle, npc.TemplateId);
					if (!perEntity.TryGetValue(key, out var perKey))
						perEntity[key] = perKey = [];

					perKey.Add((present, p.Ticks));
					sightings.Add(new SpawnSighting(c.Name, p.Ticks, spawn.Handle, npc.TemplateId, present,
						spawn.Position.X, spawn.Position.Y, spawn.Position.Z, spawn.Character.Name ?? "?"));

					// Which creature asset each flagged template wears, collected from the same spawn body so it
					// needs no DB. `monster_data` is the RDB 1010201 record whose `spawnanim` attribute names the
					// entrance animation.
					if (spawn.Character.Stats?.MonsterData is > 0 and var md)
					{
						monsterDataByTemplate[npc.TemplateId] = (uint)md;
						if (present)
							flaggedTemplates.Add(npc.TemplateId);
					}
				}

				// The death side of the respawn test, read the way `--report death-timing` reads it: the
				// BehaviorControlCenter record with BehaviorId 0x1F at phase 0x0004, whose actor is the dying handle.
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;
					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || bcc.Seq != DeathSeq || bcc.BehaviorId != DeathBehaviorId)
						continue;

					deaths.Add((c.Name, bcc.Actor, p.Ticks));
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

			// The claim only means something if it holds per creature too: a name that appears in both partitions and
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
			RespawnTest(sightings, deaths);
			SpawnAnimAssetCheck(rdbDir, monsterDataByTemplate, flaggedTemplates);
		}

		// Does the flag only ever appear on creatures that declare a `spawnanim` - a MonsterData (RDB 1010201)
		// attribute?
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

		// the STICKINESS test: is the flag a property of the entity, or a transient state of it?
		//
		// With the "this entity is arriving" reading dead, the two survivors are:
		//   CAPABILITY - this creature plays a spawn-in animation, others do not. Then a given entity carries the flag
		//                on every sighting, and a re-stream (walking back into interest range) repeats it.
		//   state      - the entity is currently inside its spawn-animation window, i.e. it respawned seconds ago.
		//                Then the same entity carries it on some sightings and not others, and the flagged one comes
		//                first - a later re-stream finds the animation finished.
		//
		// Neither the field census nor the burst partition can see this, because both aggregate over entities. It only
        // shows up when one entity's own sightings are read in order.
		//
		// Only entities sighted more than once can say anything, and a session-scoped handle is the identity - so
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

			// If it is a state that DECAYS, the flagged sighting is the first one. A mixed entity whose flag turns on
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

			// How long the window is, bounded above only. The gap measured is flagged-sighting -> first unflagged
			// one, and a sighting happens when the player walks back into range - so the true window ends somewhere
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

		// The other end of the bracket. An entity flagged on two sightings was still inside its window at the
		// later one, so the gap between them is a lower bound - the one number the upper-bound distribution above
		// cannot give, and the reason the window had only ever been bounded from one side.
		//
		// Only entities with no unflagged sighting in between count. Retail RECYCLES a handle, so
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

			// The two bounds, CROSSED per template: the smallest upper bound against the largest lower bound. If a
			// creature's own window were one number - which is what "the length of its `spawnanim` asset" would mean -
			// then for every template max-lower <= min-upper, and a template that breaks it has no single window.
			//
			// Suggestive, not conclusive, and the report says so where it prints: the lower bounds it draws on are
			// five co-streaming entities in one capture, which is one event rather than five observations.
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

		// One captured NPC spawn body, flattened: the identity, the moment, the place and the flag. A flat list rather
		// than the per-entity map the stickiness test uses, because the respawn test's whole point is that a
		// respawned creature does not keep its handle, so it has to be matched on (template, position) instead.
		private readonly record struct SpawnSighting(string Capture, long Ticks, uint Handle, uint Template, bool Flag, float X, float Y, float Z, string Name);

		// The respawn test - the one axis the two surviving sender rules disagree on.
		//
		// The flag is a one-shot "play your entrance animation now" and it decays. What neither of those settles is
		// when retail sets it, and two rules fit different halves of the evidence:
		//
		//   1. CLEARED once DELIVERED - a per-entity latch dropped after the first spawn body that carries it.
		//   2. A FLAT time window after the entity is created or respawned.
		//
		// Both predict "on for the first sighting, off after", which is why 4,687 entities could not tell them apart.
		// They differ on one thing that is in the corpus: whether a respawn re-arms it. Rule 2 says the window
		// opens at (re)creation, so the first sighting of a creature that just respawned is flagged - at a rate far
		// above the ~6% base rate. Rule 1's latch says nothing about respawns at all, so a post-respawn sighting is
		// flagged only if it is that entity's first delivery, which for a re-streamed area it is not.
		//
		// A respawned creature does not keep its handle (retail recycles them), so "the same entity after its
		// death" cannot be found by handle. It is matched on (template, position): a respawn puts the same template
		// back where it stood. Both halves are printed - the same-handle one first, because if it were populated it
		// would be the cleaner measurement, and saying it is empty is what justifies the positional one.
		private static void RespawnTest(List<SpawnSighting> sightings, List<(string Capture, uint Handle, long Ticks)> deaths)
		{
			Console.WriteLine();
			Console.WriteLine("⭐⭐ THE RESPAWN TEST (#803) - does a RESPAWN re-arm the flag?");
			Console.WriteLine();
			Console.WriteLine($"  {deaths.Count:N0} captured NPC death record(s) (BehaviorId 0x1F phase 0x0004) across {sightings.Count:N0} spawn bodies.");

			if (deaths.Count == 0 || sightings.Count == 0)
			{
				Console.WriteLine("  ⛔ Nothing to cross - the corpus holds no deaths, so this cannot be asked here.");
				return;
			}

			// The base rate this has to beat. Every entity's first sighting, which is the population a post-respawn
			// first sighting belongs to - comparing against the all-spawns rate would mix in the re-streams that the
			// stickiness test already showed are almost never flagged.
			var firstByEntity = sightings
				.GroupBy(s => (s.Capture, s.Handle, s.Template))
				.Select(g => g.OrderBy(s => s.Ticks).First())
				.ToList();
			var baseFlagged = firstByEntity.Count(s => s.Flag);
			Console.WriteLine($"  BASE RATE: {baseFlagged:N0} of {firstByEntity.Count:N0} first-sightings carry the flag = {baseFlagged * 100.0 / firstByEntity.Count:F1}%.");
			Console.WriteLine();

			// --- half 1: the same handle, sighted again after its own death.
			var deathsByHandle = deaths.GroupBy(d => (d.Capture, d.Handle))
				.ToDictionary(g => g.Key, g => g.Select(d => d.Ticks).Order().ToList());

			var samePost = new List<SpawnSighting>();
			foreach (var g in sightings.GroupBy(s => (s.Capture, s.Handle, s.Template)))
			{
				if (!deathsByHandle.TryGetValue((g.Key.Capture, g.Key.Handle), out var died))
					continue;

				var ordered = g.OrderBy(s => s.Ticks).ToList();
				samePost.AddRange(ordered.Where(s => died.Any(t => t < s.Ticks)));
			}

			Console.WriteLine($"  same HANDLE, sighted after its own death record: {samePost.Count} sighting(s)"
				+ (samePost.Count == 0
					? " - as expected: retail recycles a handle, so a respawn is a new entity."
					: $", {samePost.Count(s => s.Flag)} flagged = {samePost.Count(s => s.Flag) * 100.0 / samePost.Count:F1}%."));

			// --- half 2: the same template, back at the same PLACE, after a death there.
			//
			// The death record names only a handle, so the dead creature's template and position come from that
			// handle's last sighting before it died. A later sighting of the same template within `NearMetres` of
			// that spot, in the same capture, is the respawn.
			var byCaptureTemplate = sightings.GroupBy(s => (s.Capture, s.Template))
				.ToDictionary(g => g.Key, g => g.OrderBy(s => s.Ticks).ToList());

			var matched = new List<(SpawnSighting Dead, SpawnSighting Respawn, double Seconds)>();
			var unresolvedActor = 0;
			var noRespawnSeen = 0;

			foreach (var (capture, handle, ticks) in deaths)
			{
				var last = sightings.Where(s => s.Capture == capture && s.Handle == handle && s.Ticks <= ticks)
					.OrderBy(s => s.Ticks).LastOrDefault();
				if (last.Capture == null)
				{
					unresolvedActor++; // a player death, or a creature streamed before the dump starts
					continue;
				}

				if (!byCaptureTemplate.TryGetValue((capture, last.Template), out var candidates))
				{
					noRespawnSeen++;
					continue;
				}

				var respawn = candidates.FirstOrDefault(s => s.Ticks > ticks && s.Handle != handle && Near(s, last));
				if (respawn.Capture == null)
				{
					noRespawnSeen++;
					continue;
				}

				matched.Add((last, respawn, (respawn.Ticks - ticks) / (double)TimeSpan.TicksPerSecond));
			}

			Console.WriteLine();
			Console.WriteLine($"  same TEMPLATE back within {NearMetres}m of where one died, later in the same capture:");
			Console.WriteLine($"    {matched.Count} respawn(s) matched; {unresolvedActor} death(s) whose actor never spawned in the dump"
				+ $" (player deaths / pre-dump streams); {noRespawnSeen} with no matching later spawn.");

			if (matched.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine("  ⛔ The corpus contains NO observed respawn, so it cannot decide between the two rules.");
				Console.WriteLine("     That is a property of how the dumps were taken - a player kills something and moves on - and");
				Console.WriteLine("     no amount of re-reading them will produce one. Deciding this needs a capture that STAYS PUT");
				Console.WriteLine("     across a respawn timer, or the client's own sender-side code, neither of which exists yet.");
				return;
			}

			var respawnFlagged = matched.Count(m => m.Respawn.Flag);
			Console.WriteLine($"    of those respawns, {respawnFlagged} carry the flag = {respawnFlagged * 100.0 / matched.Count:F1}%"
				+ $" (flat base rate {baseFlagged * 100.0 / firstByEntity.Count:F1}%).");

			// the control, and it is not optional. The matched respawns are not a random sample of creatures - they
			// are the ones a player farmed, and the raw lift clusters hard by name (every Hatchling flagged, every
			// Tortage Pirate not). If Hatchlings were simply a high-flag creature, "respawns are flagged" would be
			// "the creature people kill is a flagged creature" wearing a different hat. So the comparison is against
			// each template's own first-sighting rate - the excess-over-chance form.
			//
			// And the matched respawns are EXCLUDED from that rate. Leaving them in is circular in the direction
			// that hides the effect: 13 of the 15 flagged Hatchling first-sightings are these respawns, so an
			// uncorrected control credits the hypothesis' own evidence to chance and reports 86.7% expected for a
			// creature whose non-respawn rate is 71%.
			var matchedKeys = matched.Select(m => (m.Respawn.Capture, m.Respawn.Handle, m.Respawn.Template)).ToHashSet();
			var control = firstByEntity.Where(s => !matchedKeys.Contains((s.Capture, s.Handle, s.Template)))
				.GroupBy(s => s.Template)
				.ToDictionary(g => g.Key, g => (Flagged: g.Count(s => s.Flag), Total: g.Count()));

			var expected = matched.Sum(m => control.TryGetValue(m.Respawn.Template, out var r) && r.Total > 0
				? r.Flagged / (double)r.Total
				: baseFlagged / (double)firstByEntity.Count);

			Console.WriteLine($"    ⭐ per-TEMPLATE control (matched respawns EXCLUDED from the control population):");
			Console.WriteLine($"       {expected:F1} flagged EXPECTED from these same templates' other first-sightings, {respawnFlagged} observed.");
			Console.WriteLine();
			Console.WriteLine("    creature                       template     respawns SET   OTHER first-sightings SET");
			foreach (var g in matched.GroupBy(m => m.Respawn.Template).OrderByDescending(g => g.Count()))
			{
				var r = control.GetValueOrDefault(g.Key);
				var name = g.First().Dead.Name;
				Console.WriteLine($"    {(name.Length > 28 ? name[..28] : name),-28}  {g.Key,-10}  {g.Count(m => m.Respawn.Flag),6}/{g.Count(),-6}"
					+ $"      {r.Flagged,5}/{r.Total,-7} {(r.Total == 0 ? 0 : r.Flagged * 100.0 / r.Total),5:F1}%");
			}

			Console.WriteLine();
			Console.WriteLine("    creature                        gap to respawn   flag");
			foreach (var m in matched.OrderBy(m => m.Seconds).Take(30))
				Console.WriteLine($"    {(m.Dead.Name.Length > 28 ? m.Dead.Name[..28] : m.Dead.Name),-28}  {m.Seconds,10:F1}s   {(m.Respawn.Flag ? "SET" : "-")}");

			Console.WriteLine();
			Console.WriteLine("  ⚠️ The gap is DEATH -> the spawn body's arrival, and a spawn body arrives when the PLAYER is in");
			Console.WriteLine("     range, not when the creature respawned. So it is an UPPER bound on the creature's age at");
			Console.WriteLine("     delivery, and a long gap with no flag is not evidence of a short window - it may be an old");
			Console.WriteLine("     creature streamed late. Only the SHORT gaps carry information, and they are the flagged ones.");
			Console.WriteLine();

			Console.WriteLine(respawnFlagged > expected * 2
				? "  ⭐ A respawn RE-ARMS the flag, and it survives the per-template control: the window opens at\n"
				+ "     (re)creation, which is rule 2 - and it makes the 7.3% login-burst rate the fraction of a zone\n"
				+ "     inside its respawn window rather than a paradox."
				: "  ⛔ The raw lift does NOT survive the per-template control - it is the creatures that get farmed being\n"
				+ "     the creatures that carry the flag, not the respawn. Rule 2 gains nothing from the corpus.");
		}

		// How close a respawn has to land to count as "the same spawn point". Deliberately generous: retail's own
		// spawn points scatter within a radius, and a tighter bound would trade a real respawn for a clean number.
		private const double NearMetres = 15;

		private static bool Near(SpawnSighting a, SpawnSighting b)
		{
			var dx = a.X - b.X;
			var dy = a.Y - b.Y;
			var dz = a.Z - b.Z;

			return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= NearMetres;
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
