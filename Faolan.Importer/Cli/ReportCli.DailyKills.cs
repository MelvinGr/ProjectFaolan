using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;

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
	// ⚠️ **1000069 counts a SUBSET - about a third of deaths (0.16..0.36 per capture) - and does NOT clamp at its
	// stated cap of 5** (observed to 13). So `DailyPvEKillsCap` = 5 is not that counter's clamp, and what the subset
	// IS remains open (Gitea #887).
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
