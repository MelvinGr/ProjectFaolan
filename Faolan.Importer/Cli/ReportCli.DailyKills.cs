using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.Combat;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report daily-kills` (#887): what do stats 1000069 and 1000070 actually COUNT?
	//
	// The client's stat table names them `DailyPvEKills` and `DailyPvPKills`, and the puzzle was that the PvP-named
	// one moves 118 times in a corpus with no PvP in it, caps at exactly 20, and outruns the PvE-named one two to
	// one in the same session. Two readings had been eliminated - the pairing is not off by one, and they are not
	// one stream under two names - leaving "why does 1000069 run to 13 against its own cap of 5".
	//
	// ⭐ **The question none of that asked is the simplest one: does either counter move once per KILL?** Both are
	// monotone +1 counters, so counting a capture's INCREMENTS against the NPC deaths in the same capture says what
	// each one counts, and no amount of reasoning about the caps can.
	//
	// ⭐⭐ **1000070 is a witnessed-NPC-death counter with a hard clamp at 20.** Two separate facts, and the report
	// separates them because either alone is ambiguous:
	//   * WHAT: where a capture's deaths stay under 20, its increments EQUAL the death count exactly (9=9, 10=10,
	//     11=11, ... on 7 of 15);
	//   * WHERE IT STOPS: four captures run 29, 34, 37 and 39 deaths and the counter stops dead on 20 in every one.
	//
	// ⚠️ So it counts PvE deaths under a PvP name. The name is the CLIENT's; what the server puts in the slot is
	// what this measures, and they disagree. `DailyPvPKillsCap` = 20 is the clamp, so the id pairing is right and the
	// naming is what is off - which is the opposite way round from the swap that was already rejected.
	//
	// ⭐⭐ **1000069 is the daily FATALITY count** (#887, 2026-08-25). It used to read as "a SUBSET - about a third of
	// deaths - and it does not clamp at its stated cap of 5". The subset is the fatality roll: the report's direct
	// pairing puts every one of its 49 bumps within twelve packets AFTER the two Fatality self-buffs land on the
	// player (5070797 + 791221, the pair FatalityService applies on a fatality kill) and every one of the 49 applies
	// before a bump - a bijection, both ways, over every capture. The lifetime `1379 PvEFatalities` stat is pushed in
	// the same burst. So both counters wear the wrong name: 1000070 "DailyPvPKills" counts every witnessed kill
	// (clamped at 20) and 1000069 "DailyPvEKills" counts the fatalities among them, which is why it looked like a
	// third (the client's fatality chance is level-tiered 5%..30%) and why the same mob was counted one time and
	// not the next. `DailyPvEKillsCap` = 5 clamps nothing observable (13 fatalities in one session).
	//
	// ⚠️ The death-pairing tables further down are how this was found and are kept as the instrument, but they are
	// APPROXIMATE: the +70 push precedes its death behaviour by a few packets and drifts once a death the client
	// never saw slips the k-th-increment pairing. The direct fatality pairing above them is the measurement.
	//
	// ⭐ One asymmetry in the CLIENT that lines up with the asymmetry in the DATA: the client builds a global tweak
	// accessor for `DailyPvPKillsCap` (a `SignalTarget` at `0x02D243D8`, constructed at `0x01DF61A0`) and builds
	// **none** for `DailyPvEKillsCap`. The band that construction sits in is the PvP-XP economy run -
	// `PvPXPFromPvPCapMultiplier`, `MinigamePvEXPMultiplier`, `PvEXPFromPvPGroupPenalty`, `TWK_MinLevelForPvPXP`.
	// ⛔ That is NOT a second consumer: nothing reads the global except its own ctor and its atexit, so if anything
	// consumes it, it does so through the signal and the address route ends there. What it does establish is that
	// the two caps are not interchangeable in the client, which is the shape the clamp asymmetry has.
	//
	// ⚠️ A death here is any NPC death the client was told about, which is NOT the same as a kill the player made.
	// The report prints both, because the difference is what a per-kill counter would resolve - and note the
	// player-damage column reads 0 for a NECROMANCER, whose pets deal the damage.
	public static partial class ReportCli
	{
		// Shared with --report corpse-timing / death-timing: the BCC pair a death arrives as.
		private const uint DailyDeathBehaviorId = 0x0000001F;
		private const ushort DailyDeathSeq = 0x0004;

		private const uint DailyPvEKillsStat = 1000069;
		private const uint DailyPvPKillsStat = 1000070;

		private static void DailyKills(IReadOnlyList<NamedCapture> captures)
		{
			Console.WriteLine("=== what do 1000069 / 1000070 count? (#887) ===\n");
			Console.WriteLine($"  {"capture",-46} {"1000069",9} {"1000070",9} {"peak70",7} {"deaths",7} {"by player",10}");
			Console.WriteLine($"  {new string('-', 46)} {new string('-', 9)} {new string('-', 9)} {new string('-', 7)} {new string('-', 7)} {new string('-', 10)}");

			var rows = new List<(string Capture, int PvE, int PvP, uint PvPMax, int Deaths, int ByPlayer)>();
			foreach (var capture in captures)
			{
				var deaths = new HashSet<uint>();
				var damagedByPlayer = new HashSet<uint>();
				var playerHandles = new HashSet<uint>();
				var series = new Dictionary<uint, List<uint>>();

				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, SubOpcode: FullPlayerCharacterCodec.Opcode } && p.Record.Length >= 12)
						playerHandles.Add(p.Record.ReadU32Be(8));

					if (p is not { IsIirCarrier: true })
						continue;

					if (p.SubOpcode == StatCodec.Opcode && p.TryParse(StatCodec.Parse, out var stat)
					    && stat.StatId is DailyPvEKillsStat or DailyPvPKillsStat)
					{
						if (!series.TryGetValue(stat.StatId, out var values))
							series[stat.StatId] = values = [];

						values.Add(stat.Value);
					}
					else if (p.SubOpcode == BehaviorControlCenterCodec.Opcode && p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc)
					         && bcc is { Seq: DailyDeathSeq, BehaviorId: DailyDeathBehaviorId })
					{
						deaths.Add(bcc.Actor);
					}
					else if (p is { IsDownload: true } && p.SubOpcode == DamageResultCodec.Opcode && p.Body.Length > 4)
					{
						try
						{
							var hit = DamageResultCodec.Parse(p.Body.AsSpan(4));
							if (playerHandles.Contains(hit.Attacker))
								damagedByPlayer.Add(hit.Target);
						}
						catch
						{
							// a truncated record - counted by its absence, not by a wrong number
						}
					}
				}

				var pve = Increments(series.GetValueOrDefault(DailyPvEKillsStat));
				var pvp = Increments(series.GetValueOrDefault(DailyPvPKillsStat));
				if (pve == 0 && pvp == 0)
					continue;

				var pvpMax = series.GetValueOrDefault(DailyPvPKillsStat)?.Max() ?? 0;
				var byPlayer = deaths.Count(d => damagedByPlayer.Contains(d));
				rows.Add((capture.Name, pve, pvp, pvpMax, deaths.Count, byPlayer));
				Console.WriteLine($"  {(capture.Name.Length > 46 ? capture.Name[..46] : capture.Name),-46} {pve,9} {pvp,9} {pvpMax,7} {deaths.Count,7} {byPlayer,10}");
			}

			Console.WriteLine();
			Console.WriteLine($"  totals over {rows.Count} capture(s): 1000069 +{rows.Sum(r => r.PvE)}, 1000070 +{rows.Sum(r => r.PvP)}, "
			                  + $"{rows.Sum(r => r.Deaths)} NPC deaths, {rows.Sum(r => r.ByPlayer)} the player damaged");
			Console.WriteLine();

			// ⭐ THE FINDING. Where the capture's deaths stay under 20, 1000070's increments EQUAL them exactly; where
			// the deaths run past 20, the counter stops dead on 20 and the deaths carry on. That is a witnessed-kill
			// counter with a hard clamp, and it is two separate facts - what it counts, and where it stops - that the
			// equality alone would not have separated.
			var under = rows.Where(r => r.Deaths < 20).ToList();
			Console.WriteLine($"  1000070 == the capture's NPC deaths, where deaths < 20 : {under.Count(r => r.PvP == r.Deaths)}/{under.Count}");
			foreach (var r in rows.Where(r => r.PvPMax >= 20))
				Console.WriteLine($"    CLAMPED: {r.Capture[..Math.Min(46, r.Capture.Length)]} stops at {r.PvPMax} with {r.Deaths} deaths in the capture");

			Console.WriteLine();
			Console.WriteLine($"  1000069 == the capture's NPC deaths : {rows.Count(r => r.PvE == r.Deaths)}/{rows.Count}   (it counts a SUBSET - see the ratio)");
			var withBoth = rows.Where(r => r is { PvE: > 0, Deaths: > 0 }).ToList();
			Console.WriteLine($"  1000069 as a fraction of deaths, per capture: {string.Join(" ", withBoth.Select(r => $"{(double)r.PvE / r.Deaths:F2}"))}");
			Console.WriteLine();
			Console.WriteLine("  ⚠️ These are DAILY totals, so a session joined mid-day starts wherever the day left it - which is");
			Console.WriteLine("     why the comparison is against a capture's own INCREMENTS and not against its final value.");

			DailyKillAttribution(captures);
		}

		// ⭐ The SUBSET question, asked the only way it can be answered: attribute each increment to the death that
		// precedes it in the same capture, then compare the deaths that earned a +1 with the ones that did not. A
		// per-capture ratio ("about a third") cannot say WHICH third; a time-aligned table can.
		//
		// An increment is attributed to the latest not-yet-attributed NPC death at or before it in packet order. The
		// distance (in packets) is printed beside each attribution so a match that is really a coincidence is visible
		// - a counter that follows its death by a handful of packets is an event, one that lands hundreds later is
		// not.
		private static void DailyKillAttribution(IReadOnlyList<NamedCapture> captures)
		{
			Console.WriteLine();
			Console.WriteLine("=== attribution: which deaths does each counter follow? ===");
			Console.WriteLine("  columns: +69/+70 = the counter that moved right after this death (distance in packets), dmg = who damaged");
			Console.WriteLine("  the mob (P = the player's own hits/casts, S = another spawned entity - a pet or a guard - U = an unknown");
			Console.WriteLine("  handle, - = nobody the capture saw), gap = player level minus mob level");

			var counted69 = new List<(bool Player, bool Spawned, int Gap, string Name, bool Loot, bool Fatal, bool Quest, bool FoughtBack)>();
			var uncounted = new List<(bool Player, bool Spawned, int Gap, string Name, bool Loot, bool Fatal, bool Quest, bool FoughtBack)>();
			var counted70 = 0;
			var distances69 = new List<int>();
			int bumps69 = 0, bumpsAfterFatality = 0, fatalityApplies = 0, fatalitiesBumped = 0;

			foreach (var capture in captures)
			{
				var playerHandles = new HashSet<uint>();
				var playerLevel = 0u;
				var npcLevel = new Dictionary<uint, uint>();
				var npcName = new Dictionary<uint, string>();
				foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
				{
					var spawnBody = e.Data is { Length: >= 12 } ? e.Data[(12 + (int)e.Data.ReadU32Be(8))..] : null;
					if (spawnBody != null && SpawnBodyReader.TryReadCharacterMessage(spawnBody, out var read) && read.Message.Stats is { } st)
						npcLevel[e.Handle] = st.Level;
					if (!string.IsNullOrEmpty(e.Name))
						npcName[e.Handle] = e.Name;
				}

				var deaths = new List<(int Index, uint Actor)>();
				var attackers = new Dictionary<uint, HashSet<uint>>();
				// Per-kill OUTCOMES, by packet index: a loot bag spawning, a fatality self-buff landing on the player,
				// a quest-goal tick. Each is matched to the death it follows within a short window, to ask whether
				// 1000069 is counting an outcome rather than a property of the mob.
				var lootBags = new List<int>();
				var fatalities = new List<int>();
				var questTicks = new List<int>();
				// Mobs that landed a hit or a cast on a PLAYER - i.e. fought back - keyed by mob handle.
				var hitPlayer = new HashSet<uint>();
				// Every damage event ON a target, in order: what the killing blow was, and how many hits it took.
				var damageLog = new Dictionary<uint, List<(int Index, char Kind, uint Attacker, uint Spell)>>();
				// The player's level as the session goes - the beach dings several times in one capture.
				var levelAt = new List<(int Index, uint Level)>();
				var moves69 = new List<(int Index, int Delta)>();
				var moves70 = new List<(int Index, int Delta)>();
				uint? last69 = null, last70 = null;

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p is not { IsIirCarrier: true })
						continue;

					if (p.SubOpcode == FullPlayerCharacterCodec.Opcode && p.Record.Length >= 12)
					{
						playerHandles.Add(p.Record.ReadU32Be(8));
						var sheet = SubClassStatBlock.ExtractFromSnapshot(p.Record);
						var lvl = sheet?.Find(kv => kv.Id == (uint)CharacterStatId.Level).Value ?? 0;
						if (lvl is > 0 and <= 80 && playerLevel == 0)
							playerLevel = lvl;
					}
					else if (p.SubOpcode == StatCodec.Opcode && p.TryParse(StatCodec.Parse, out var stat))
					{
						if (stat.StatId == (uint)CharacterStatId.Level && playerHandles.Contains(stat.Handle) && stat.Value is > 0 and <= 80)
							levelAt.Add((i, stat.Value));
						if (stat.StatId == DailyPvEKillsStat)
						{
							if (last69 is { } prev && stat.Value > prev)
								moves69.Add((i, (int)(stat.Value - prev)));
							else if (last69 is null && stat.Value > 0 && fatalities.Any(f => i - f is >= 0 and <= 12))
								moves69.Add((i, 1)); // a session's FIRST push of the counter, arriving on the heels of a fatality
							last69 = stat.Value;
						}
						else if (stat.StatId == DailyPvPKillsStat)
						{
							if (last70 is { } prev && stat.Value > prev)
								moves70.Add((i, (int)(stat.Value - prev)));
							last70 = stat.Value;
						}
					}
					else if (p.SubOpcode == BehaviorControlCenterCodec.Opcode && p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc)
					         && bcc is { Seq: DailyDeathSeq, BehaviorId: DailyDeathBehaviorId })
					{
						if (!deaths.Any(d => d.Actor == bcc.Actor && i - d.Index < 50))
							deaths.Add((i, bcc.Actor));
					}
					else if (p is { IsDownload: true, SubOpcode: WorldObjectClasses.LootBag })
					{
						lootBags.Add(i);
					}
					else if (p is { IsDownload: true, SubOpcode: QuestGoalCodec.Opcode })
					{
						questTicks.Add(i);
					}
					else if (p.SubOpcode == MagicBuffCodec.Opcode && p.TryParse(MagicBuffCodec.Parse, out var buff)
					         && buff.Form == MagicBuffIds.FormApply && buff.SpellBaseId == FatalityService.FatalityBuffA && playerHandles.Contains(buff.ApplyTo))
					{
						// ⚠️ Not gated on IsDownload - that flag marks the COMPRESSED frames, not the s2c direction, and
						// the fatality applies ride uncompressed. The 0.1 s marker buff is the one counted; its 10 s
						// sibling lands in the same frame and would double the count.
						fatalities.Add(i);
					}
					else if (p is { IsDownload: true } && p.SubOpcode == DamageResultCodec.Opcode && p.Body.Length > 4)
					{
						try
						{
							var hit = DamageResultCodec.Parse(p.Body.AsSpan(4));
							(attackers.TryGetValue(hit.Target, out var a) ? a : attackers[hit.Target] = []).Add(hit.Attacker);
							(damageLog.TryGetValue(hit.Target, out var dl) ? dl : damageLog[hit.Target] = []).Add((i, 'M', hit.Attacker, 0));
							if (playerHandles.Contains(hit.Target))
								hitPlayer.Add(hit.Attacker);
						}
						catch
						{
							// truncated - see above
						}
					}
					else if (p is { IsDownload: true } && p.SubOpcode == StatDamageCodec.Opcode && p.Body.Length > 4)
					{
						try
						{
							var cast = StatDamageCodec.Parse(p.Body.AsSpan(4));
							if (cast.Caster != 0 && cast.Handle != cast.Caster)
							{
								(attackers.TryGetValue(cast.Handle, out var a) ? a : attackers[cast.Handle] = []).Add(cast.Caster);
								(damageLog.TryGetValue(cast.Handle, out var dl) ? dl : damageLog[cast.Handle] = []).Add((i, 'S', cast.Caster, cast.SpellBaseId));
								if (playerHandles.Contains(cast.Handle))
									hitPlayer.Add(cast.Caster);
							}
						}
						catch
						{
							// truncated - see above
						}
					}
				}

				if (moves69.Count == 0 && moves70.Count == 0 && fatalities.Count == 0)
					continue;

				// ⭐⭐ THE DIRECT MEASUREMENT - no death pairing involved. A 1000069 bump and the fatality buffs on the
				// player arrive in the same burst (buffs first, bump a few packets later), so pair them straight.
				var bumpIdx = moves69.SelectMany(m => Enumerable.Repeat(m.Index, m.Delta)).ToList();
				var usedBumps = new HashSet<int>();
				foreach (var f in fatalities)
				{
					fatalityApplies++;
					var hit = bumpIdx.Select((b, n) => (b, n)).Where(x => x.b - f is >= -2 and <= 12 && !usedBumps.Contains(x.n)).Select(x => (int?)x.n).FirstOrDefault();
					if (hit is { } n)
					{
						usedBumps.Add(n);
						fatalitiesBumped++;
					}
				}
				bumps69 += bumpIdx.Count;
				bumpsAfterFatality += usedBumps.Count;

				// Attribute. ⭐ 1000070 follows EVERY death about twenty packets later (until it clamps), so its k-th
				// increment IS the k-th death - that pairing is exact and needs no window. A 1000069 increment rides the
				// same refresh: on a counted kill it is pushed a few packets BEFORE the 1000070 bump of that kill. So
				// a +69 is attributed to the death whose +70 push is nearest to it; past the clamp (no +70 any more) it
				// falls back to the latest death before it. "Latest death before the +69" alone was one kill off,
				// because the +69 lands before the +70 of the kill it belongs to, not after.
				var by69 = new Dictionary<int, int>(); // death index -> distance
				var by70 = new Dictionary<int, int>();
				var pushOfDeath = new Dictionary<int, int>(); // death index -> index of its +70 push
				var ordered70 = moves70.SelectMany(m => Enumerable.Repeat(m.Index, m.Delta)).ToList();
				for (var k = 0; k < Math.Min(ordered70.Count, deaths.Count); k++)
				{
					by70[deaths[k].Index] = ordered70[k] - deaths[k].Index;
					pushOfDeath[deaths[k].Index] = ordered70[k];
				}
				var taken69 = new HashSet<int>();
				foreach (var (idx, delta) in moves69)
					for (var n = 0; n < delta; n++)
					{
						int? pick = null;
						var best = int.MaxValue;
						foreach (var (di, push) in pushOfDeath)
						{
							var dist = Math.Abs(push - idx);
							if (dist <= 40 && dist < best && !taken69.Contains(di))
							{
								best = dist;
								pick = di;
							}
						}
						pick ??= deaths.Where(d => d.Index <= idx && !taken69.Contains(d.Index)).OrderByDescending(d => d.Index).Select(d => (int?)d.Index).FirstOrDefault();
						if (pick is not { } chosen)
							break;
						taken69.Add(chosen);
						by69[chosen] = idx - chosen;
					}

				Console.WriteLine();
				Console.WriteLine($"  --- {capture.Name} (player level {(playerLevel == 0 ? "?" : playerLevel.ToString())}; {deaths.Count} deaths, 1000069 +{moves69.Sum(m => m.Delta)}, 1000070 +{moves70.Sum(m => m.Delta)})");
				foreach (var (index, actor) in deaths)
				{
					var att = attackers.GetValueOrDefault(actor) ?? [];
					var byPlayer = att.Any(playerHandles.Contains);
					var bySpawned = att.Any(h => !playerHandles.Contains(h) && npcLevel.ContainsKey(h));
					var byUnknown = att.Any(h => !playerHandles.Contains(h) && !npcLevel.ContainsKey(h));
					var dmg = (byPlayer ? "P" : "") + (bySpawned ? "S" : "") + (byUnknown ? "U" : "");
					if (dmg.Length == 0)
						dmg = "-";
					var lvl = npcLevel.TryGetValue(actor, out var l) ? (int)l : -1;
					var gap = playerLevel > 0 && lvl >= 0 ? (int)playerLevel - lvl : int.MinValue;
					var name = npcName.GetValueOrDefault(actor) ?? $"0x{actor:X}";
					var c69 = by69.TryGetValue(index, out var d69) ? $"+69({d69})" : "";
					var c70 = by70.TryGetValue(index, out var d70) ? $"+70({d70})" : "";
					// Outcomes within the 60 packets after the death (a bag spawns and a fatality buff lands within a few).
					var loot = lootBags.Any(b => b >= index && b - index <= 60);
					// The fatality buffs land on the KILLER a dozen-odd packets BEFORE the victim's death behaviour goes out
					// (the +69 push sits between them), so the window opens well before the death index.
					var fatal = fatalities.Any(f => f >= index - 40 && f - index <= 60);
					var quest = questTicks.Any(q => q >= index && q - index <= 60);
					var foughtBack = hitPlayer.Contains(actor);
					var fight = (damageLog.GetValueOrDefault(actor) ?? []).Where(e => e.Index <= index + 5).ToList();
					var blow = fight.Count == 0 ? "?" : fight[^1].Kind == 'M' ? (playerHandles.Contains(fight[^1].Attacker) ? "M" : "m") : (playerHandles.Contains(fight[^1].Attacker) ? $"S{fight[^1].Spell}" : $"s{fight[^1].Spell}");
					var playerHits = fight.Count(e => playerHandles.Contains(e.Attacker));
					var lvlNow = levelAt.Where(l => l.Index <= index).Select(l => (uint?)l.Level).LastOrDefault() ?? playerLevel;
					var outcome = $"pl{lvlNow} kb={blow,-9} hits={playerHits,-2} " + (foughtBack ? "hitP " : "") + (loot ? "loot " : "") + (fatal ? "FATAL " : "") + (quest ? "quest" : "");
					Console.WriteLine($"    #{index,-6} {name,-28} lvl {(lvl < 0 ? "?" : lvl.ToString()),-3} gap {(gap == int.MinValue ? "?" : gap.ToString()),-4} dmg {dmg,-3} {c69,-10} {c70,-10} {outcome}");

					var row = (byPlayer, bySpawned, gap == int.MinValue ? 999 : gap, name, loot, fatal, quest, foughtBack);
					if (by69.ContainsKey(index))
					{
						counted69.Add(row);
						distances69.Add(d69);
					}
					else
					{
						uncounted.Add(row);
					}
					if (by70.ContainsKey(index))
						counted70++;
				}
			}

			Console.WriteLine();
			Console.WriteLine("=== 1000069 vs the FATALITY buffs on the player (direct pairing, no death matching) ===");
			Console.WriteLine($"  1000069 bumps: {bumps69}, preceded within 12 packets by a fatality apply on the player: {bumpsAfterFatality}");
			Console.WriteLine($"  fatality applies on the player: {fatalityApplies}, followed within 12 packets by a 1000069 bump: {fatalitiesBumped}");
			Console.WriteLine("  (an unpaired apply is a fatality whose counter push the capture did not carry - e.g. a login-restored buff)");
			Console.WriteLine();
			Console.WriteLine("=== 1000069: the deaths it followed vs the deaths it ignored (death pairing is approximate - see above) ===");
			Console.WriteLine($"  attributed: {counted69.Count} (median distance {(distances69.Count == 0 ? 0 : distances69.OrderBy(d => d).ElementAt(distances69.Count / 2))} packets, max {(distances69.Count == 0 ? 0 : distances69.Max())}); ignored: {uncounted.Count}; 1000070 attributed: {counted70}");
			Console.WriteLine($"  {"",-30} {"counted",8} {"ignored",8}");
			Console.WriteLine($"  {"damaged by the player",-30} {counted69.Count(r => r.Player),8} {uncounted.Count(r => r.Player),8}");
			Console.WriteLine($"  {"damaged by another spawn",-30} {counted69.Count(r => r.Spawned && !r.Player),8} {uncounted.Count(r => r.Spawned && !r.Player),8}");
			Console.WriteLine($"  {"no seen damage",-30} {counted69.Count(r => !r.Player && !r.Spawned),8} {uncounted.Count(r => !r.Player && !r.Spawned),8}");
			Console.WriteLine($"  {"a loot bag spawned after it",-30} {counted69.Count(r => r.Loot),8} {uncounted.Count(r => r.Loot),8}");
			Console.WriteLine($"  {"a FATALITY buff on the player",-30} {counted69.Count(r => r.Fatal),8} {uncounted.Count(r => r.Fatal),8}");
			Console.WriteLine($"  {"a quest-goal tick after it",-30} {counted69.Count(r => r.Quest),8} {uncounted.Count(r => r.Quest),8}");
			Console.WriteLine($"  {"the mob hit the player",-30} {counted69.Count(r => r.FoughtBack),8} {uncounted.Count(r => r.FoughtBack),8}");
			Console.WriteLine($"  {"the mob never touched the player",-30} {counted69.Count(r => !r.FoughtBack),8} {uncounted.Count(r => !r.FoughtBack),8}");
			foreach (var band in new[] { ("gap <= 0 (mob at/above player)", (Func<int, bool>)(g => g <= 0)), ("gap 1..3", g => g is >= 1 and <= 3), ("gap 4..6", g => g is >= 4 and <= 6), ("gap 7+", g => g is >= 7 and < 999), ("gap unknown", g => g == 999) })
				Console.WriteLine($"  {band.Item1,-30} {counted69.Count(r => band.Item2(r.Gap)),8} {uncounted.Count(r => band.Item2(r.Gap)),8}");
			Console.WriteLine();
			Console.WriteLine("  by mob, counted / total:");
			foreach (var g in counted69.Concat(uncounted).GroupBy(r => r.Name).OrderByDescending(g => g.Count()).Take(25))
				Console.WriteLine($"    {g.Key,-30} {counted69.Count(r => r.Name == g.Key),4} / {g.Count(),-4}");
		}


		// A monotone counter's ups. Restatements at the same value are how the wire carries it and are not events.
		private static int Increments(List<uint>? values)
		{
			if (values is null)
				return 0;

			var moves = 0;
			for (var i = 1; i < values.Count; i++)
				if (values[i] > values[i - 1])
					moves += (int)(values[i] - values[i - 1]);

			return moves;
		}
	}
}
