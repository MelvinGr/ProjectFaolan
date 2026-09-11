using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Constants;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report respawn-interval`: what SELECTS a creature's respawn interval - 120 s or 150 s.
	//
	// `--report corpse-timing` established that retail respawns and roughly how fast, and the finding it cost a cycle
	// to reach is that a respawn comes back on a NEW entity id: `death(H)` then `spawn(H)` finds nothing at all and
	// reads as "retail never respawns". The signal is the same creature reappearing at the dead one's spawn point.
	// This report owns that measurement now and asks the next question - which property of the creature, or of the
	// place, picks the number - by carrying every attribute the spawn body states alongside each interval.
	//
	// ## What can fake an interval, and what actually rules each one out
	//
	//  * A sibling that was there all along. A camp holds eight of one creature within a few units of each other;
	//    the player walks out of range and back, and every one of them re-spawns to the client. Matched by name and
	//    position alone, the first of those reads as a respawn seconds after a kill. Ruled out by requiring the fill
	//    to be a handle the client has never seen before in this capture - a new entity, not a re-stream of a live
	//    one. (This is what corpse-timing's Hatchling cluster of 0.9-13.0 s "intervals" partly was.)
	//
	//  * A creature that came back while the player was away. A spawn body arrives when the client is told, and
	//    it is told when the player is in range - so death -> delivery is an UPPER bound on the interval unless the
	//    player was watching. The test for that is not where the player stood, it is whether the intervals track
	//    their own deaths. If four pirates die over 30 s and every fill lands at one moment - the player's return -
	//    then the intervals are spread as widely as the deaths were. If instead each fill lands the same distance
	//    after its own death while the deaths themselves are minutes apart, no single delivery event can explain it
	//    and the constant is the timer. So each creature is scored on `deaths spread` against `intervals spread`, and
	//    that is a fact about the timestamps alone - no assumption about range anywhere in it.
	//
	//    A player-position watch gate was built first and thrown away, and the reason is worth keeping. Judged
	//    against a fixed 100 u radius, every clean 150.0 s Brutal Pirate came back "unwatched" (the player wanders
	//    107-138 u mid-fight) while a Hatchling nest and a wandering Panther passed - the gate selected exactly the
	//    wrong matches. Calibrating the radius off the corpus instead does not rescue it: the delivery distances have
	//    a median of 104 u but a p90 of 533 u and a max of 1,914 u, because a teleport moves the player without a
	//    `CharDCMove` and the login burst is the map's whole population rather than what is in range. The measurement
	//    it would need - how far retail actually streams a spawn - is its own open question, and our 150 u
	//    `PlayerInterestRange` is still an invented number.
	//
	//  * A different creature of the same kind wandering in. Ruled out only partly, by distance: retail's revive
	//    is exact and every clean match lands at 0.0 u, so a fill several units off its anchor is suspect. Each
	//    match's distance is printed rather than filtered away. The fill's own `spawn_animation` flag rides along as
	//    an independent witness - retail sets it on a (re)created entity - though its absence proves nothing, since
	//    it is under-sent.
	//
	// And there is no bound in the other direction. "The player stood there for N s and nothing came back" cannot
	// distinguish a missing respawn from a delivery the client was out of range for - the capture that shows
	// `Brutal Pirate 0xaa88` returning at 150.0 s also holds a 334 s window in which it "never returned". Windows are
	// counted here only to size the HARVEST, never as evidence against a respawn.
	//
	// Exit code is 0 always - a measurement, not a gate.
	public static partial class ReportCli
	{
		// How close a returning creature must be to the dead one's anchor to be the same spawn point. Retail's revive
		// is exact - every clean match lands at 0.0 u - so this is slack for a mob that took a step before its first
		// captured transform, not a search radius.
		private const float RespawnFillRadius = 8.0f;

		// A creature's intervals agree when they span no more than this. The measured clusters are far tighter (the
		// eight Brutal Pirates span 0.3 s over two unrelated captures), so this is deliberately loose: it is there to
		// separate a timer from a spread, not to grade one.
		private const double AgreementSeconds = 2.0;

		// ...and the deaths themselves must be at least this far apart for that agreement to mean anything. Two kills
		// seconds apart would produce two near-identical intervals from a single delivery event, which is the reading
		// this whole test exists to exclude.
		private const double DeathsApartSeconds = 20.0;

		// How much of a creature's own observations the agreeing cluster has to hold before the agreement is the
		// creature's behaviour rather than three readings picked out of a spread.
		private const double ClusterMajority = 0.6;

		// Deaths with at least this much capture left after them are the population an interval could have been
		// harvested from. Sizing only - see the above.
		private const double HarvestWindowSeconds = 160.0;

		private static void RespawnInterval(IReadOnlyList<NamedCapture> captures)
		{
			var matches = new List<RespawnMatch>();
			var misses = new List<string>();

			// Is the non-observation a property of the solo playfield rather than of a long timer? Every anchored
			// death is recorded with the map its creature lives on so the two can be separated.
			var deathsByMap = new Dictionary<uint, int>();
			var matchesByMap = new Dictionary<uint, int>();

			// The control on the Solo split: a map with deaths and no respawns proves nothing if none of those
			// deaths had enough capture after it to see one. This counts the deaths that did.
			var harvestableByMap = new Dictionary<uint, int>();
			var deathsTotal = 0;
			var deathsAnchored = 0;
			var harvestable = 0;

			foreach (var c in captures)
			{
				var (sightings, deaths, endSeconds) = RespawnScan(c);
				var firstSeen = sightings.GroupBy(s => s.Handle).ToDictionary(g => g.Key, g => g.Min(s => s.Seconds));
				var claimed = new HashSet<uint>();

				foreach (var death in deaths.OrderBy(d => d.Seconds))
				{
					deathsTotal++;

					// The death record names a handle and nothing else, so the dead creature's identity and both of its
					// anchors come from its own spawn bodies: HOME is where the client first saw it, DIED is where it
					// last was. They differ for anything that was pulled, and retail revives at the spawn point - so a
					// fill is matched against whichever is nearer and the report says which.
					var own = sightings.Where(s => s.Handle == death.Handle && s.Seconds <= death.Seconds).ToList();
					if (own.Count == 0)
						continue; // a player death, or a creature streamed before the dump opened

					deathsAnchored++;

					var home = own[0].Position;
					var died = own[^1].Position;
					var dead = own[^1];
					var deadMap = MapOfTemplate(dead.Template);
					if (endSeconds - death.Seconds >= HarvestWindowSeconds)
					{
						harvestable++;
						harvestableByMap[deadMap] = harvestableByMap.GetValueOrDefault(deadMap) + 1;
					}

					var fill = sightings.FirstOrDefault(s => s.Seconds > death.Seconds
					                                        && s.Handle != death.Handle
					                                        && s.Template == dead.Template
					                                        && !claimed.Contains(s.Handle)
					                                        // NEW to the client: this spawn is the handle's first
					                                        // sighting. Without it a live sibling re-streamed after an
					                                        // interest round-trip reads as the respawn.
					                                        && firstSeen.GetValueOrDefault(s.Handle, -1) == s.Seconds
					                                        && MathF.Min(Ground(s.Position - home), Ground(s.Position - died)) <= RespawnFillRadius);
					if (fill == null)
					{
						// Why this kill yielded nothing, kept rather than dropped: with five creatures carrying the
						// whole report, whether the filters are starving it is a question that has to be answered with
						// a number. `near` is the closest later spawn of the same creature whatever its distance and
						// whatever its handle's history.
						var near = sightings.Where(s => s.Seconds > death.Seconds && s.Handle != death.Handle && s.Template == dead.Template)
							.Select(s => (Distance: MathF.Min(Ground(s.Position - home), Ground(s.Position - died)), Fresh: firstSeen.GetValueOrDefault(s.Handle, -1) == s.Seconds))
							.OrderBy(x => x.Distance)
							.FirstOrDefault();
						misses.Add(near == default ? "no later spawn of this creature at all" : Miss(near.Distance, near.Fresh));
						deathsByMap[deadMap] = deathsByMap.GetValueOrDefault(deadMap) + 1;
						continue;
					}

					claimed.Add(fill.Handle);
					deathsByMap[deadMap] = deathsByMap.GetValueOrDefault(deadMap) + 1;
					matchesByMap[deadMap] = matchesByMap.GetValueOrDefault(deadMap) + 1;
					var anchor = Ground(fill.Position - home) <= Ground(fill.Position - died) ? home : died;
					matches.Add(new RespawnMatch(c.Name, dead, fill, death.Seconds, fill.Seconds - death.Seconds,
						Ground(fill.Position - anchor), anchor == home ? "home" : "died"));
				}
			}

			Console.WriteLine($"=== respawn interval over {captures.Count} capture(s) ===");
			Console.WriteLine();
			Console.WriteLine($"{deathsTotal} NPC death record(s), {deathsAnchored} of which name a creature the dump also spawned;");
			Console.WriteLine($"{harvestable} of those have >= {HarvestWindowSeconds:0} s of capture left after them - the ceiling on what this corpus can yield.");
			Console.WriteLine();
			Console.WriteLine($"MATCHES: {matches.Count} - the same TEMPLATE back within {RespawnFillRadius:0} u of where one died, on a handle new to the client.");
			Console.WriteLine();
			Console.WriteLine("  interval  dist  anchor  fresh  died at  lvl  creature                  template   capture");
			foreach (var m in matches.OrderBy(m => m.Seconds))
				Console.WriteLine($"  {m.Seconds,8:0.0}s {m.Distance,5:0.0}u  {m.Anchor,-6}  {(m.Fill.Fresh ? "yes" : "-"),-5}  {m.DeathSeconds,7:0}s"
				                  + $"  {m.Dead.Level,3}  {Trim(m.Dead.Name, 24),-24}  {m.Dead.Template,-9}  {Trim(m.Capture, 42)}");

			SoloPlayfieldSplit(deathsByMap, matchesByMap, harvestableByMap);

			Console.WriteLine();
			Console.WriteLine($"the {misses.Count} anchored death(s) that matched NOTHING, by what was closest (would loosening the filters pay?):");
			foreach (var g in misses.GroupBy(m => m).OrderByDescending(g => g.Count()))
				Console.WriteLine($"  {g.Count(),4}  {g.Key}");

			var timers = Timers(matches);
			SelectorCrossTab(timers);
		}

		// Which of the matched creatures has a measured TIMER. Three things have to hold, and each rules out a
		// different way for a constant to be an artifact:
		//
		//   * the intervals AGREE - at least two of them within `AgreementSeconds` of the shortest;
		//   * that agreeing cluster holds most of the creature's observations, not a cherry-picked few. A delivery
		//     delay can only add time, so a creature with a timer piles up at one value and trails off to the right;
		//     three tight readings inside a spread of 3.4-15.4 s is a continuum, not a pile;
		//   * and the agreement cannot be one delivery event seen several times - either the deaths behind it are
		//     `DeathsApartSeconds` apart (one return cannot land at a fixed offset from each of them) or the cluster
		//     spans two captures, which is a stronger version of the same argument: two sessions share nothing.
		private static List<CreatureTimer> Timers(List<RespawnMatch> matches)
		{
			Console.WriteLine();
			Console.WriteLine("=== is the interval a TIMER, or the moment the player came back? ===");
			Console.WriteLine();
			Console.WriteLine($"  Qualifying: >= 2 intervals within {AgreementSeconds:0} s of the shortest, holding >= {ClusterMajority * 100:0}% of the creature's");
			Console.WriteLine($"  observations, and either their deaths >= {DeathsApartSeconds:0} s apart or the cluster spanning two captures.");
			Console.WriteLine();
			Console.WriteLine("  creature                  template   n  caps  intervals                              agree  worst  deaths span  verdict");

			var timers = new List<CreatureTimer>();
			foreach (var g in matches.GroupBy(m => m.Dead.Template).OrderBy(g => g.Min(m => m.Seconds)))
			{
				var all = g.OrderBy(m => m.Seconds).ToList();
				var cluster = all.Where(m => m.Seconds - all[0].Seconds <= AgreementSeconds).ToList();
				var clusterCaptures = cluster.Select(m => m.Capture).Distinct().Count();
				var deathsSpan = cluster.Count < 2
					? 0
					: cluster.GroupBy(m => m.Capture).Max(c => c.Max(m => m.DeathSeconds) - c.Min(m => m.DeathSeconds));

				var isTimer = cluster.Count >= 2
				              && cluster.Count >= all.Count * ClusterMajority
				              && (deathsSpan >= DeathsApartSeconds || clusterCaptures >= 2);

				var interval = Median(cluster.Select(m => m.Seconds).ToList());
				var verdict = isTimer
					? $"⭐ TIMER {interval:0} s"
					: cluster.Count < 2
						? "one clean observation only"
						: cluster.Count < all.Count * ClusterMajority
							? $"scattered - only {cluster.Count} of {all.Count} agree"
							: $"deaths {deathsSpan:0} s apart in one capture - could be one delivery";

				// The cluster's worst anchor distance, and it earns its column: retail's revive is exact, so a
				// creature whose "respawns" all land a few units off its spawn point (Panther, 3.6-6.9 u) is a
				// wandering neighbour of the same kind, while every measured timer sits at 0.0 u.
				var worst = cluster.Count == 0 ? 0 : cluster.Max(m => m.Distance);
				Console.WriteLine($"  {Trim(all[0].Dead.Name, 24),-24}  {g.Key,-9} {all.Count,2}  {all.Select(m => m.Capture).Distinct().Count(),4}  "
				                  + $"{Trim(string.Join(", ", all.Select(m => m.Seconds.ToString("0.0"))), 36),-36}  {cluster.Count,2}/{all.Count,-2}  "
				                  + $"{worst,4:0.0}u  {deathsSpan,9:0} s  {verdict}");

				if (isTimer)
					timers.Add(new CreatureTimer(cluster.Select(m => m.Dead).ToList(), interval, cluster.Count, clusterCaptures));
			}

			Console.WriteLine();
			Console.WriteLine("  ⚠️ A creature that fails is not thereby shown to have no timer - it is shown to have no MEASUREMENT.");
			Console.WriteLine("     A delivery delay only ever lengthens an interval, so each creature's SHORTEST reading stays the best");
			Console.WriteLine("     estimate of its timer; it is just not one this corpus can stand behind.");
			return timers;
		}

		// the question. Every measured interval carries the whole npc_stats/stats attribute set its spawn
		// body states, so the split can be tested against each candidate at once rather than argued from two
		// creatures.
		//
		// An attribute SURVIVES as a selector only if it is a function of the interval in the strict sense: never two
		// creatures sharing its value while disagreeing on the interval. Anything that merely varies within one
		// interval group is not refuted (a rule may map several values to one number), which is why the table calls
		// out only the direction that can only be a defect - the same value, two different intervals.
		private static void SelectorCrossTab(List<CreatureTimer> timers)
		{
			Console.WriteLine();
			Console.WriteLine("=== ⭐ what SELECTS the interval (#855) ===");
			Console.WriteLine();

			if (timers.Count < 2)
			{
				Console.WriteLine($"  {timers.Count} creature(s) with a measured timer - nothing to cross-tab yet.");
				Console.WriteLine("  This closes on more captures, not on more re-reading.");
				return;
			}

			Console.WriteLine($"  {timers.Count} creature(s) with a measured timer:");
			Console.WriteLine();
			Console.WriteLine("  interval  n  caps  creature                  template   map    lvl   hp       comm  fact  sfam  rank  nset  proft  race  mdata");
			foreach (var t in timers.OrderBy(t => t.Interval))
			{
				var d = t.First;
				Console.WriteLine($"  {t.Interval,7:0.0}s {t.Observations,2}  {t.Captures,4}  {Trim(d.Name, 24),-24}  {d.Template,-9}  {Set(t, s => s.MapId),-5}  {Set(t, s => s.Level),-4}  {Set(t, s => s.Health),-7}"
				                  + $"  {Set(t, s => s.Community),-4}  {Set(t, s => s.FactionId),-4}  {Set(t, s => s.SuperFamily),-4}  {Set(t, s => s.NpcType),-4}  {Set(t, s => s.Normalset),-4}  {Set(t, s => s.ProfessionType),-5}  {Set(t, s => s.Race),-4}  {Set(t, s => s.MonsterData)}");
			}

			var groups = timers.GroupBy(t => Math.Round(t.Interval / 5.0) * 5.0).OrderBy(g => g.Key).ToList();
			Console.WriteLine();
			Console.WriteLine($"  {groups.Count} distinct interval group(s): {string.Join(", ", groups.Select(g => $"{g.Key:0}s x{g.Count()}"))}");
			if (groups.Count < 2)
			{
				Console.WriteLine("  One group - every measured creature shares an interval, so nothing here can select between two.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine("  attribute      values by interval group                                       verdict");

			foreach (var (name, of) in SelectorAttributes)
			{
				var byGroup = groups.Select(g => (Interval: g.Key, Values: g.SelectMany(t => t.Values(of)).Distinct().OrderBy(v => v).ToList())).ToList();
				var shared = byGroup.SelectMany(g => g.Values).GroupBy(v => v).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
				// An attribute that is 0 in every group is UNSET on these creatures, not a selector that failed:
				// calling it refuted would credit the report with a finding where it has no reading at all.
				var verdict = byGroup.All(g => g.Values is [0])
					? "unset on every measured creature - carries no information"
					: shared.Count > 0
					? $"⛔ REFUTED - {string.Join("/", shared)} spans two intervals"
					: byGroup.All(g => g.Values.Count == 1)
						? "survives, and is CONSTANT within each group"
						: "survives (varies within a group, so only as part of a coarser rule)";

				var rendered = string.Join("  |  ", byGroup.Select(g => $"{g.Interval:0}s: {string.Join(",", g.Values)}"));
				Console.WriteLine($"  {name,-13}  {Trim(rendered, 68),-68}  {verdict}");
			}

			Console.WriteLine();
			Console.WriteLine("  ⚠️ 'survives' is not 'is the rule'. With this few creatures every attribute that happens to differ");
			Console.WriteLine("     between the groups survives, and only a REFUTED line is a fact that will not move. The cross-tab");
			Console.WriteLine("     earns its keep as captures are added: each new creature at a known interval kills more of them.");
		}

		// The candidate selectors, all read off the spawn body itself so this needs no DB. `level`/`health` are the
		// scaling reading, `community`/`faction` the "whose camp is it" reading, `superfamily`/`race`/`monsterdata`
		// the creature-family reading, `npc_type`/`normalset` the rank reading (Minion/MiniBoss/Boss, Prey), and
		// `map` the one that is not about the creature at all - the playfield's own rule.
		private static readonly (string Name, Func<RespawnSighting, uint> Of)[] SelectorAttributes =
		[
			("map", s => s.MapId),
			("level", s => s.Level),
			("health", s => s.Health),
			("community", s => s.Community),
			("faction", s => s.FactionId),
			("superfamily", s => s.SuperFamily),
			("npc_type", s => s.NpcType),
			("normalset", s => s.Normalset),
			("profession", s => s.ProfessionType),
			("race", s => s.Race),
			("monsterdata", s => s.MonsterData),
		];

		// Everything one capture says about creatures arriving and dying.
		private static (List<RespawnSighting> Sightings, List<(double Seconds, uint Handle)> Deaths, double EndSeconds) RespawnScan(NamedCapture c)
		{
			var sightings = new List<RespawnSighting>();
			var deaths = new List<(double Seconds, uint Handle)>();
			var start = c.Packets.Count > 0 ? c.Packets.Min(p => p.Ticks) : 0L;
			var end = c.Packets.Count > 0 ? (c.Packets.Max(p => p.Ticks) - start) / (double)TimeSpan.TicksPerSecond : 0;
			uint map = 0;

			foreach (var p in c.Packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true })
					continue;

				var seconds = (p.Ticks - start) / (double)TimeSpan.TicksPerSecond;

				switch (p.SubOpcode)
				{
					case PlayfieldFCodec.Opcode:
						// A MapChange we cannot read clears the map rather than leaving the previous one standing
						//  - keeping it re-attributes everything that follows to the playfield just left.
						map = p.TryParse(PlayfieldFCodec.Parse, out var change) ? change.MapId : 0;
						break;

					case BehaviorControlCenterCodec.Opcode:
					{
						// players are not creatures: a player's death is followed by their own resurrect spawn, which
						// would read as the fastest respawn in the corpus. Same handle line as NpcSpawnExtractor.
						if (p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) && bcc.Seq == CorpseDeathSeq && bcc.BehaviorId == CorpseDeathBehaviorId
						    && bcc.Actor < EntityHandles.CharacterInstanceBase)
							deaths.Add((seconds, bcc.Actor));

						break;
					}

					case SimpleCharFullUpdateCodec.Opcode:
					{
						if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) || spawn.Character?.npc_stats is not { } npc || spawn.Character.player_stats != null)
							break;

						var stats = spawn.Character.Stats;
						sightings.Add(new RespawnSighting(seconds, spawn.Handle, npc.TemplateId, spawn.Character.Name ?? "?", spawn.Position, map)
						{
							// retail sets this on a (re)CREATED entity, so a fill carrying it was built at about
							// the moment it was delivered - an independent witness. Absence says nothing (it is
							// under-sent), which is why it is printed beside the interval rather than filtering.
							Fresh = npc.ShouldSerializeSpawnAnimation(),
							Level = stats?.Level ?? 0,
							Health = stats?.Health ?? 0,
							Race = stats?.Race ?? 0,
							MonsterData = stats?.MonsterData ?? 0,
							Community = npc.Community,
							FactionId = npc.FactionId,
							SuperFamily = npc.SuperFamily,
							NpcType = npc.NpcType,
							Normalset = npc.Normalset,
							ProfessionType = npc.ProfessionType,
						});
						break;
					}
				}
			}

			return (sightings, deaths, end);
		}

		private static string Miss(float distance, bool fresh)
		{
			var where = distance <= RespawnFillRadius ? $"within {RespawnFillRadius:0} u" : distance <= 50 ? "8-50 u away" : distance <= 300 ? "50-300 u away" : "> 300 u away";
			return $"closest later {(fresh ? "NEW-handle" : "re-streamed")} spawn of it was {where}";
		}

		private static string Set(CreatureTimer timer, Func<RespawnSighting, uint> of)
		{
			return string.Join("/", timer.Values(of));
		}

		private static double Median(List<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			return sorted[sorted.Count / 2];
		}

		private sealed record RespawnSighting(double Seconds, uint Handle, uint Template, string Name, Vector3 Position, uint MapId)
		{
			public bool Fresh { get; init; }
			public uint Level { get; init; }
			public uint Health { get; init; }
			public uint Race { get; init; }
			public uint MonsterData { get; init; }
			public uint Community { get; init; }
			public uint FactionId { get; init; }
			public uint SuperFamily { get; init; }
			public uint NpcType { get; init; }
			public uint Normalset { get; init; }
			public uint ProfessionType { get; init; }
		}

		// Creature template -> the map its placements sit on, loaded once. 0 when the creature has no placement at
		// all - an ambient-pool or summoned type, which is a real answer rather than a lookup failure.
		private static Dictionary<uint, uint>? _templateMaps;

		private static uint MapOfTemplate(uint template)
		{
			if (_templateMaps == null)
			{
				using var db = OpenDb();
				_templateMaps = db.NpcSpawns.GroupBy(n => n.NpcTemplateId)
					.Select(g => new { g.Key, Map = g.Min(n => n.MapId) })
					.ToDictionary(x => x.Key, x => x.Map);
			}

			return _templateMaps.GetValueOrDefault(template);
		}

		/// <summary>
		///     Are the creatures that never respawn simply the ones on a solo playfield?
		/// </summary>
		/// <remarks>
		///     The hypothesis is that an open-world creature on a Solo map does not come back for the life of the
		///     instance, which would make the right model a per-map FLAG rather than any timer. It matters because
		///     Tortage Island (4010) is one of only four Solo maps and is where nearly every capture was taken - so
		///     "no respawn observed" could be about the playfield rather than about the interval.
		///     <para />
		///     A map with deaths and no matches is not by itself evidence: it may simply have no capture long
		///     enough after them. Read it beside the interval table - a map that yields a measured timer has settled
		///     the question for itself.
		/// </remarks>
		private static void SoloPlayfieldSplit(Dictionary<uint, int> deathsByMap, Dictionary<uint, int> matchesByMap, Dictionary<uint, int> harvestableByMap)
		{
			Console.WriteLine();
			Console.WriteLine("=== ⭐ is the non-observation about the SOLO playfield rather than the timer? (#855) ===");
			Console.WriteLine();
			Console.WriteLine($"  {"map",6}  {"solo",5}  {"deaths",7}  {"watched",8}  {"respawns",9}  name");

			using var db = OpenDb();
			var names = db.Maps.ToDictionary(m => m.Id, m => m.Name);
			foreach (var (map, deaths) in deathsByMap.OrderByDescending(kv => kv.Value))
			{
				var solo = SoloMaps.Contains(map);
				Console.WriteLine($"  {(map == 0 ? "-" : map.ToString()),6}  {(solo ? "SOLO" : ""),5}  {deaths,7}  {harvestableByMap.GetValueOrDefault(map),8}  {matchesByMap.GetValueOrDefault(map),9}  "
				                  + (map == 0 ? "(creature has no placement - ambient or summoned)" : names.GetValueOrDefault(map, "?")));
			}

			var soloDeaths = deathsByMap.Where(kv => SoloMaps.Contains(kv.Key)).Sum(kv => kv.Value);
			var soloMatches = matchesByMap.Where(kv => SoloMaps.Contains(kv.Key)).Sum(kv => kv.Value);
			var openDeaths = deathsByMap.Where(kv => !SoloMaps.Contains(kv.Key) && kv.Key != 0).Sum(kv => kv.Value);
			var openMatches = matchesByMap.Where(kv => !SoloMaps.Contains(kv.Key) && kv.Key != 0).Sum(kv => kv.Value);

			Console.WriteLine();
			var soloWatched = harvestableByMap.Where(kv => SoloMaps.Contains(kv.Key)).Sum(kv => kv.Value);
			var openWatched = harvestableByMap.Where(kv => !SoloMaps.Contains(kv.Key) && kv.Key != 0).Sum(kv => kv.Value);
			Console.WriteLine($"  SOLO maps:  {soloDeaths,4} death(s), {soloWatched,4} watched >= {HarvestWindowSeconds:0}s, {soloMatches,3} respawn(s)");
			Console.WriteLine($"  OPEN maps:  {openDeaths,4} death(s), {openWatched,4} watched >= {HarvestWindowSeconds:0}s, {openMatches,3} respawn(s)");
			Console.WriteLine();
			// The open maps that also yield nothing despite being watched. They are what stops this being a clean
			// solo-vs-open split, and naming them is the difference between a finding and an overclaim.
			var silentOpen = harvestableByMap
				.Where(kv => kv.Key != 0 && !SoloMaps.Contains(kv.Key) && kv.Value > 0 && matchesByMap.GetValueOrDefault(kv.Key) == 0)
				.OrderByDescending(kv => kv.Value)
				.ToList();

			if (soloMatches > 0)
			{
				Console.WriteLine("  ⛔ REFUTED: a Solo map DOES respawn, so the non-observation elsewhere is about the timer or the");
				Console.WriteLine("     sample rather than the playfield, and #855's per-map-flag alternative is out.");
			}
			else if (soloWatched > 0 && openMatches > 0)
			{
				Console.WriteLine($"  ⭐ The Solo maps yield NOTHING from {soloWatched} deaths watched long enough to see a respawn, while the open");
				Console.WriteLine($"     maps yield {openMatches} from {openWatched}. So 'too little capture time' does not explain the Solo silence.");

				if (silentOpen.Count > 0)
				{
					Console.WriteLine();
					Console.WriteLine("  ⚠️ ...but it is NOT a clean Solo-vs-open split, and this is the part to weigh before believing it:");
					foreach (var kv in silentOpen)
						Console.WriteLine($"       map {kv.Key} is OPEN, {kv.Value} death(s) watched, and yields none either");

					Console.WriteLine("     So 'does not respawn' is not a property of the Solo MODE - some open maps behave the same way.");
					Console.WriteLine("     What the data supports is a per-MAP or per-CREATURE rule, of which Solo may only be a correlate.");
				}

				Console.WriteLine();
				Console.WriteLine("  ⚠️ And one Solo map carries the whole Solo column: 4010 is Tortage Island, a story zone as well as a");
				Console.WriteLine("     Solo one, so 'Solo' and 'this particular playfield' are confounded here and cannot be separated.");
			}
			else
			{
				Console.WriteLine("  ⚠️ Neither: the split has no watched Solo deaths or no open respawns to compare, so it says nothing yet.");
			}
			Console.WriteLine($"  ⚠️ `watched` is the control: a map with deaths and no respawns says nothing unless those deaths had"
			                  + $" at least {HarvestWindowSeconds:0}s of capture after them, which is the window a respawn could appear in.");
		}

		// The four playfields the server runs in `MapInstanceMode.Solo`.
		private static readonly HashSet<uint> SoloMaps = [4010, 5001, 5007, 5008];

		private sealed record RespawnMatch(string Capture, RespawnSighting Dead, RespawnSighting Fill, double DeathSeconds, double Seconds, float Distance, string Anchor);

				// `Sightings`, not one representative: `level` is 32 on one Wild Cow and 33 on the next, so an attribute
		// read off a single spawn would be silently one of several values. The cross-tab compares sets.
		private sealed record CreatureTimer(List<RespawnSighting> Sightings, double Interval, int Observations, int Captures)
		{
			public RespawnSighting First => Sightings[0];

			public List<uint> Values(Func<RespawnSighting, uint> of)
			{
				return Sightings.Select(of).Distinct().OrderBy(v => v).ToList();
			}
		}
	}
}
