using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report quest-xp`: what retail pays for finishing a quest, mined out of the whole corpus.
	//
	// Completing a quest awards no experience and no money on our server. The columns are imported and nothing reads
	// them, and the obvious sources cannot be wired: `XpReward`/`MoneyReward` are populated on a handful of quests
	// and are zero on every quest of the one chain the corpus watches being completed, the RDB catalog's per-line
	// reward disagrees with the observed value and its cadence, and the client's tuning table holds no reward curve
	// (the level curve is server-side here too). What is needed is more talk-completed quest
	// completions, from different lines and levels - new captures, or mine the corpus".
	//
	// This is the mining half, done systematically rather than by hand.
	//
	// ## The one thing that has to be right: kills contaminate
	//
	// A quest that completes on a kill completes at the same instant the kill pays combat XP, so the experience
	// delta around it is the kill's, not the quest's. Any correlation that does not exclude those fits a formula to
	// combat XP - quest 13479 shows +20 in six captures and +40 in a seventh, and it is a kill quest. So every
	// completion here is classified by whether an NPC death record sits inside the window the delta is measured over,
	// and the two populations are never mixed.
	//
	// The DELTA is measured across the completion, not from a running total: `Exp` arrives in `StatUpdateIIR` stat
	// blocks whenever anything touches it, so the reading either side of the completion is what the corpus states, and
	// a completion with no `Exp` record after it yields nothing rather than a zero.
	//
	// Exit code is 0 always - a measurement.
	public static partial class ReportCli
	{
		// How close a death has to be to a completion for the completion's experience delta to be the kill's. Wide on
		// purpose: this decides which observations are THROWN AWAY, and a contaminated one kept is worse than a clean
		// one lost.
		private const double KillContaminationWindowSeconds = 12.0;

		// How close a level-up ding has to sit to a completion for the ding to be that completion's award. Retail
		// sends both inside one burst - the play4 rescue dings in the same 0.1 s - so this is tight on purpose: a
		// second apart is a different event, and a wrong pairing would state a bound about the wrong award.
		private const double DingCoincidenceSeconds = 2.0;

		private static void QuestXp(IReadOnlyList<NamedCapture> captures)
		{
			var observations = new List<QuestPayout>();
			var bounds = new List<(string Capture, uint QuestId, long AtLeast, uint Level)>();
			var completionsSeen = 0;
			var expFromStatBlock = 0;
			var expFromDing = 0;
			var statPackets = 0;
			var statParseFailures = 0;
			var statBlocks = 0;
			var statRecords = 0L;
			var statIds = new HashSet<CharacterStatId>();

			foreach (var c in captures)
			{
				var start = c.Packets.Count > 0 ? c.Packets.Min(p => p.Ticks) : 0L;
				var exp = new List<(double Seconds, uint Value)>();
				var cash = new List<(double Seconds, ulong Value)>();
				var deaths = new List<double>();
				var dings = new List<(double Seconds, uint Exp, uint LevelBase)>();
				var completions = new List<(double Seconds, uint QuestId)>();
				uint level = 0;

				foreach (var p in c.Packets)
				{
					if (!p.IsDownload)
						continue;

					var seconds = (p.Ticks - start) / (double)TimeSpan.TicksPerSecond;

					// The player's CASH - an ABSOLUTE balance in TIN, not a delta: `[u64 balanceTin][u32 reason]`.
					// Two things here are load-bearing and both were wrong first time round. The balance is a U64
					// read at offset 0 (taking a u32 at 4 lands mid-field and yields nonsense deltas of ~-1e9), and
					// this wire opcode is shared with `TradepostInterface`'s search results - the money body is
					// disambiguated by being exactly 12 bytes, which is what the wallet decode records.
					if (p.WireOpcode == CashWireOpcode && p.Body.Length == 12)
					{
						cash.Add((seconds, p.Body.ReadU64Be(0)));
						continue;
					}

					if (!p.IsIirCarrier)
						continue;

					switch (p.SubOpcode)
					{
						case StatUpdateCodec.Opcode:
						{
							// the control, and it counts the packets that REACH here rather than the ones that
							// parse. Counting after the parse cannot tell "no packet carried this opcode" from "every
							// parse failed" - both read as zero, and zero is exactly the answer that would be believed.
							statPackets++;

							// Only the player has an Exp stat, so no handle filter is needed - and adding one off
							// the spawn set would drop the updates that arrive before the player's own spawn body.
							if (!p.TryParse(StatUpdateCodec.Parse, out var stat))
							{
								statParseFailures++;
								break;
							}

							statBlocks++;
							statRecords += stat.Stats.Records.Count;
							foreach (var (id, _) in stat.Stats.Records)
								statIds.Add(id);

							foreach (var (id, raw) in stat.Stats.Records)
							{
								if (id == CharacterStatId.Exp)
								{
									exp.Add((seconds, raw));
									expFromStatBlock++;
								}
								else if (id == CharacterStatId.Level && raw > 0)
								{
									level = raw;
								}
							}

							break;
						}

						case BehaviorControlCenterCodec.Opcode:
						{
							if (p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) && bcc.Seq == CorpseDeathSeq && bcc.BehaviorId == CorpseDeathBehaviorId)
								deaths.Add(seconds);

							break;
						}

						case QuestCodec.Opcode:
						{
							if (p.TryParse(QuestCodec.Parse, out var q) && q.State == QuestCodec.StateCompleted)
							{
								completions.Add((seconds, q.QuestId));
								completionsSeen++;
							}

							break;
						}

						// The other place a running Exp total reaches the client: the level-up ding carries
						// `[Exp, ExpCurrentLevelBase, MaxExp, ...]` for the level just reached. It fires only on a
						// level-up, so it is a sparse source - but a quest completion that levels the character
						// states its own new total exactly.
						case NewLevelCodec.Opcode:
						{
							if (p.TryParse(NewLevelCodec.Parse, out var ding) && ding.Deltas.Length > 1 && ding.Deltas[0] > 0)
							{
								exp.Add((seconds, ding.Deltas[0]));
								dings.Add((seconds, ding.Deltas[0], ding.Deltas[1]));
								expFromDing++;
								level = ding.NewLevel;
							}

							break;
						}
					}
				}

				foreach (var (seconds, questId) in completions)
				{
					var before = exp.LastOrDefault(e => e.Seconds <= seconds);
					var after = exp.FirstOrDefault(e => e.Seconds > seconds);
					if (before == default || after == default)
						continue;

					var killNear = deaths.Any(d => Math.Abs(d - seconds) <= KillContaminationWindowSeconds);
					var cashBefore = cash.LastOrDefault(x => x.Seconds <= seconds);
					var cashAfter = cash.FirstOrDefault(x => x.Seconds > seconds && x.Seconds <= after.Seconds + KillContaminationWindowSeconds);
					long? cashDelta = cashBefore == default || cashAfter == default ? null : (long)cashAfter.Value - (long)cashBefore.Value;

					observations.Add(new QuestPayout(c.Name, questId, seconds, (long)after.Value - before.Value, cashDelta, killNear, level, after.Seconds - before.Seconds));
				}

				// the one THING the corpus can state about A SINGLE AWARD. A level-up ding carries the running total
				// and the threshold of the level just crossed, so the OVERSHOOT (`Exp - base`) is how far the award
				// that triggered the ding carried the player past it. The player was below the threshold immediately
				// before, so that award - and it is one award, the one that fired the ding - was at least the
				// overshoot. A quest completion in the same instant with no kill beside it makes the award the
				// quest's, which turns an unmeasurable payout into a real lower bound.
				foreach (var (seconds, questId) in completions)
				{
					var ding = dings.FirstOrDefault(d => Math.Abs(d.Seconds - seconds) <= DingCoincidenceSeconds);
					if (ding == default || deaths.Any(d => Math.Abs(d - seconds) <= KillContaminationWindowSeconds))
						continue;

					bounds.Add((c.Name, questId, ding.Exp - ding.LevelBase, level));
				}
			}

			Console.WriteLine($"=== quest payouts over {captures.Count} capture(s) ===");
			Console.WriteLine();
			// The counts before the join, because "0 usable observations" has two very different causes and only
			// one of them is a fact about retail. The first run of this printed a clean zero and the reason was that
			// the Exp readings were missing, not the completions.
			Console.WriteLine($"{completionsSeen} quest COMPLETION record(s) (QuestIIR state 4) across the corpus.");
			Console.WriteLine($"Exp readings: {expFromStatBlock} from a StatUpdate stat block, {expFromDing} from a level-up ding.");
			Console.WriteLine($"  (control: {statPackets:N0} StatUpdate packet(s) reached the reader, {statParseFailures:N0} failed to parse,");
			Console.WriteLine($"   {statBlocks:N0} yielded {statRecords:N0} record(s) over {statIds.Count} distinct stat id(s) - so a zero above");
			Console.WriteLine($"   is retail not sending Exp there rather than a scan that read nothing.)");
			Console.WriteLine($"{observations.Count} completion(s) have an Exp reading on BOTH sides - the only ones that can state a payout.");

			var clean = observations.Where(o => !o.KillNear).ToList();
			var dirty = observations.Where(o => o.KillNear).ToList();
			Console.WriteLine($"  {dirty.Count} sit within {KillContaminationWindowSeconds:0} s of an NPC death - the delta there is the KILL's, and they are excluded from everything below.");
			Console.WriteLine($"  {clean.Count} are clean (no death in the window) - the usable population.");
			Console.WriteLine();

			if (bounds.Count > 0)
			{
				Console.WriteLine($"⭐ {bounds.Count} completion(s) triggered a LEVEL-UP with no kill beside them, which bounds that quest's award");
				Console.WriteLine("   from below: the ding states the running total and the threshold just crossed, and the award that fired");
				Console.WriteLine("   it carried the player from under the threshold to the total, so it is at least the overshoot.");
				Console.WriteLine();
				Console.WriteLine("   quest     award >=   at lvl   capture");
				foreach (var b in bounds.OrderBy(b => b.QuestId))
					Console.WriteLine($"  {b.QuestId,-8}  {b.AtLeast,9:N0}   {b.Level,5}   {Trim(b.Capture, 46)}");

				Console.WriteLine();
			}

			QuestPayoutTable("CLEAN - no kill in the window", clean);
			QuestPayoutTable("EXCLUDED - a kill sits in the window, so the delta is not the quest's", dirty);
			QuestPayoutJoin(clean);
		}

		private static void QuestPayoutTable(string title, List<QuestPayout> rows)
		{
			Console.WriteLine($"--- {title}: {rows.Count} ---");
			if (rows.Count == 0)
			{
				Console.WriteLine();
				return;
			}

			Console.WriteLine();
			Console.WriteLine("   quest     xp gained   cash    lvl   span   capture");
			foreach (var o in rows.OrderBy(o => o.QuestId).ThenBy(o => o.Capture))
				Console.WriteLine($"  {o.QuestId,-8}  {o.XpDelta,10:N0}  {(o.CashDelta?.ToString("N0") ?? "-"),6}  {o.Level,4}  {o.Span,5:0.0}s  {Trim(o.Capture, 46)}");

			Console.WriteLine();
		}

		// What the shipped quest data says about each quest that was actually paid for, since the whole question is
		// which stored value (if any) the payment corresponds to.
		private static void QuestPayoutJoin(List<QuestPayout> clean)
		{
			if (clean.Count == 0)
				return;

			using var db = OpenDb();
			var ids = clean.Select(o => o.QuestId).Distinct().ToList();
			var defs = db.QuestDefinitions.Where(q => ids.Contains(q.QuestId))
				.Select(q => new { q.QuestId, q.Name, q.Level, q.QuestLineId, q.XpReward, q.MoneyReward, q.FieldF0, q.FieldF8, q.BandLevel })
				.ToList()
				.ToDictionary(q => q.QuestId);

			Console.WriteLine("=== ⭐ what the shipped data holds for each PAID quest (#820) ===");
			Console.WriteLine();
			Console.WriteLine("  quest     paid xp   cash    line   Level  BandLevel  XpReward  MoneyReward  FieldF0  FieldF8   name");
			foreach (var g in clean.GroupBy(o => o.QuestId).OrderBy(g => g.Key))
			{
				var paid = g.Select(o => o.XpDelta).Distinct().OrderBy(x => x).ToList();
				var cash = g.Select(o => o.CashDelta).Where(x => x.HasValue).Select(x => x!.Value).Distinct().OrderBy(x => x).ToList();
				if (!defs.TryGetValue(g.Key, out var d))
				{
					Console.WriteLine($"  {g.Key,-8}  {string.Join("/", paid),8}  {Trim(string.Join("/", cash), 6),6}   (no QuestDefinition row)");
					continue;
				}

				Console.WriteLine($"  {g.Key,-8}  {Trim(string.Join("/", paid), 8),8}  {Trim(string.Join("/", cash), 6),6}  {d.QuestLineId,-6} {d.Level,5}  {d.BandLevel,8}  {d.XpReward,8}  {d.MoneyReward,11}  {d.FieldF0,7}  {d.FieldF8,7}   {Trim(d.Name ?? "", 28)}");
			}

			Console.WriteLine();
			Console.WriteLine("  ⭐ The question is whether ANY column predicts the paid column. #820 already eliminated the per-quest");
			Console.WriteLine("     reward pair (zero on the captured chain), the catalog's per-LINE reward (wrong value AND cadence)");
			Console.WriteLine("     and the client's tuning table (holds no reward curve at all). What it lacked was observations.");
			Console.WriteLine();

			var distinctQuests = clean.Select(o => o.QuestId).Distinct().Count();
			var distinctLines = clean.Select(o => defs.TryGetValue(o.QuestId, out var d) ? d.QuestLineId : 0).Distinct().Count();
			var distinctLevels = clean.Select(o => o.Level).Distinct().Count();
			Console.WriteLine($"  coverage: {distinctQuests} distinct quest(s), {distinctLines} line(s), {distinctLevels} character level(s).");
			Console.WriteLine("  ⚠️ A formula needs the payments to VARY across lines and levels - several observations of one line at");
			Console.WriteLine("     adjacent levels cannot separate 'per-line rate' from 'scales with level' from 'a number we do not hold'.");
		}

		// `DispatchUpdateCash` (GameCharInterface 0x05) - the player's whole purse, restated.
		private static readonly uint CashWireOpcode = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchUpdateCash);

		private sealed record QuestPayout(string Capture, uint QuestId, double Seconds, long XpDelta, long? CashDelta, bool KillNear, uint Level, double Span);
	}
}
