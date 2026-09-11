using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report aa-xp`: what a kill pays into the ALTERNATE-ADVANCEMENT xp pool, measured rather than assumed.
	//
	// Retail ANNOUNCES every grant. A `FormatFeedbackIIR` carrying the token `AlternateAdvancementPvEXpGranted`
	// states the amount verbatim in its first `<parameter>`, and the `AAPvEXP` (1000007) `StatIIR` push in the same
	// frame moves by exactly that much. So the accrual rate was never capture-blocked - it is written out in words
	// in the on-screen feedback, and nobody had read those messages.
	//
	// The report joins each announcement to the NPC death it follows and to that mob's own spawn record, because the
	// amount is a function of the killed creature's level and not of anything about the player beyond their level:
	// the pay-out table it prints is what `AlternateAdvancementRules` ships.
	//
	// The mob's level is read off its own spawn record on the wire, never off `NpcSpawns` - a spawn row is a
	// per-respawn OBSERVATION and disagrees with the instance that was actually killed (`row_observation` and
	// `capture_name_not_character` in the agent memory: the same trap, twice).
	public static partial class ReportCli
	{
		private const uint AaPvEXpStat = (uint)CharacterStatId.AAPvEXP;
		private const uint AaPvPXpStat = (uint)CharacterStatId.AAPvPXP;

		// The death BCC pair, shared with --report daily-kills / corpse-timing.
		private const uint AaDeathBehaviorId = 0x0000001F;
		private const ushort AaDeathSeq = 0x0004;

		// The feedback tokens that carry an AA grant. The first two are the kill/quest announcements (the second
		// adds a bonus percent as its own parameter); the third rides a loot-chat line on an item redemption.
		private static readonly string[] AaGrantTokens =
		[
			"AlternateAdvancementPvEXpGranted",
			"AlternateAdvancementPvEXpGrantedWithBonusPercent",
			"AlternateAdvancementPvPXpGranted",
			"GenericRewardAAXP-NoBonus"
		];

		// How far ahead of its own kill an announcement is sent. Measured: 2..15 packets on every one of the paired
		// grants, against hundreds for the preceding kill, so the window separates the two readings outright.
		private const int AaGrantToDeathWindow = 30;

		private static readonly Regex AaParameterRx = new Regex("<parameter value=\"(?<v>[^\"]*)\"", RegexOptions.Compiled);
		private static readonly Regex AaTokenRx = new Regex("token=\"(?<t>[^\"]*)\"", RegexOptions.Compiled);

		private static async Task AaXp(IReadOnlyList<NamedCapture> captures)
		{
			Console.WriteLine("=== what a kill pays into the AA xp pool (#150) ===\n");

			// The MULTIPLIER has a column. Pull the two template words the paying mobs differ on, so the ×2 and
			// ×1.15 rows can be read off the data rather than off the creature's reputation.
			Dictionary<string, (uint? Type, uint? Flags)> byName;
			await using (var db = OpenDb())
			{
				byName = (await db.NpcTemplates.AsNoTracking()
						.Join(db.NpcStatCores.AsNoTracking(), t => t.TemplateId, c => c.NpcTemplateId, (t, c) => new { t.Name, c.NpcType, c.NpcFlags })
						.ToListAsync())
					.GroupBy(x => x.Name)
					.ToDictionary(g => g.Key, g => (g.First().NpcType, g.First().NpcFlags));
			}

			var grants = new List<(string Capture, int Index, string Token, long Amount, int? Bonus)>();
			var kills = new List<(string Capture, uint PlayerLevel, uint MobLevel, string Mob, long Amount, int? Bonus, int Distance)>();
			// each pool push against the kill it belongs to, DIRECTLY. Counting pushes and kills per capture and
			// dividing gives 2.00 on six captures and misses on three, which cannot say whether the residue is a
			// clipped window, a witnessed death or something else. Pairing says where each push actually sits.
			var pushAttribution = new List<(string Capture, int[] Pushes, (int Index, bool ByPlayer)[] Deaths, int[] Grants)>();

			var poolSeries = new List<(string Capture, uint Stat, uint First, uint Last, int Moves, long Sum, int Pushes, int Deaths, int PlayerKills, int Grants)>();

			foreach (var capture in captures)
			{
				var playerHandles = new HashSet<uint>();
				var playerLevel = 0u;
				var mobLevel = new Dictionary<uint, uint>();
				var mobName = new Dictionary<uint, string>();
				foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
				{
					var spawnBody = e.Data is { Length: >= 12 } ? e.Data[(12 + (int)e.Data.ReadU32Be(8))..] : null;
					if (spawnBody != null && SpawnBodyReader.TryReadCharacterMessage(spawnBody, out var read) && read.Message.Stats is { } st)
						mobLevel[e.Handle] = st.Level;
					if (!string.IsNullOrEmpty(e.Name))
						mobName[e.Handle] = e.Name;
				}

				var deaths = new List<(int Index, uint Actor)>();
				var localGrants = new List<(int Index, string Token, long Amount, int? Bonus)>();
				var series = new Dictionary<uint, List<(int Index, uint Value)>>();

				// the pool is restated more often than it MOVES, and the restatements track deaths rather than
				// grants. Separating the deaths the player caused from the ones they merely witnessed is what decides
				// whether the rule is "twice per kill" or "twice per witnessed death" - the two agree on every capture
				// with fewer than six deaths and diverge on the busy ones.
				var damagedByPlayer = new HashSet<uint>();

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
							playerLevel = stat.Value;
						else if (stat.StatId is AaPvEXpStat or AaPvPXpStat)
						{
							if (!series.TryGetValue(stat.StatId, out var values))
								series[stat.StatId] = values = [];

							values.Add((i, stat.Value));
						}
					}
					else if (p.SubOpcode == BehaviorControlCenterCodec.Opcode && p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc)
					         && bcc is { Seq: AaDeathSeq, BehaviorId: AaDeathBehaviorId })
					{
						if (!deaths.Any(d => d.Actor == bcc.Actor && i - d.Index < 50))
							deaths.Add((i, bcc.Actor));
					}
					else if (p is { IsDownload: true } && p.SubOpcode == DamageResultCodec.Opcode && p.Body.Length > 4)
					{
						try
						{
							var swing = DamageResultCodec.Parse(p.Body.AsSpan(4));
							if (playerHandles.Contains(swing.Attacker))
								damagedByPlayer.Add(swing.Target);
						}
						catch
						{
							// a truncated record - counted by its absence, not by a wrong number
						}
					}
					else if (p is { IsDownload: true } && p.SubOpcode == StatDamageCodec.Opcode && p.Body.Length > 4)
					{
						try
						{
							var hit = StatDamageCodec.Parse(p.Body.AsSpan(4));
							if (playerHandles.Contains(hit.Caster))
								damagedByPlayer.Add(hit.Handle);
						}
						catch
						{
							// as above
						}
					}
					else if (p.SubOpcode == FormatFeedbackCodec.Opcode && p.Body.Length > 4)
					{
						string xml;
						try
						{
							xml = System.Text.Encoding.Latin1.GetString(FormatFeedbackCodec.Parse(p.Body.AsSpan(4)).Xml);
						}
						catch
						{
							continue; // a truncated record - counted by its absence, not by a wrong number
						}

						var token = AaTokenRx.Match(xml) is { Success: true } t ? t.Groups["t"].Value : "";
						if (!AaGrantTokens.Contains(token))
							continue;

						var values = AaParameterRx.Matches(xml).Select(m => m.Groups["v"].Value).ToArray();
						var amount = AaParameterValue(values.ElementAtOrDefault(0));
						var bonus = values.Length > 1 ? (int?)AaParameterValue(values[1]) : null;
						if (amount is null)
							continue;

						localGrants.Add((i, token, amount.Value, bonus));
					}
				}

				foreach (var g in localGrants)
				{
					grants.Add((capture.Name, g.Index, g.Token, g.Amount, g.Bonus));

					// The kill the announcement belongs to: the latest death at or before it. A grant that follows no
					// death in the same capture is a quest turn-in or an item redemption, and stays out of the table.
					// The grant leads its own kill: the announcement is sent a handful of packets before the dying
					// creature's LimboDeath behaviour, so the death to pair with is the next one, not the preceding
					// one. Pairing backwards attributes every amount to the PREVIOUS kill and reads as a table
					// shifted by one level - which is exactly what it produced (57 against level 73, 234 against 78)
					// before the distances were printed. `pair_with_the_burst`, again.
					var death = deaths.Where(d => d.Index >= g.Index).OrderBy(d => d.Index).FirstOrDefault();
					if (death.Actor == 0 || death.Index - g.Index > AaGrantToDeathWindow)
						continue;

					kills.Add((capture.Name, playerLevel, mobLevel.GetValueOrDefault(death.Actor),
						mobName.GetValueOrDefault(death.Actor, $"handle {death.Actor:X8}"), g.Amount, g.Bonus, death.Index - g.Index));
				}

				foreach (var (stat, values) in series)
				{
					var moves = 0;
					long sum = 0;
					for (var i = 1; i < values.Count; i++)
					{
						if (values[i].Value <= values[i - 1].Value)
							continue;

						moves++;
						sum += values[i].Value - values[i - 1].Value;
					}

					poolSeries.Add((capture.Name, stat, values[0].Value, values[^1].Value, moves, sum, values.Count, deaths.Count,
						deaths.Count(d => damagedByPlayer.Contains(d.Actor)), localGrants.Count));

					if (stat == AaPvEXpStat)
					{
						pushAttribution.Add((capture.Name, values.Select(v => v.Index).ToArray(),
							deaths.Select(d => (d.Index, damagedByPlayer.Contains(d.Actor))).ToArray(),
							localGrants.Select(g => g.Index).ToArray()));
					}
				}
			}

			Console.WriteLine($"  {AaGrantTokens.Length} grant token(s) swept over {captures.Count} capture(s): {grants.Count} announcement(s).\n");
			if (grants.Count == 0)
			{
				Console.WriteLine("  none - this corpus holds no AA grant.");
				return;
			}

			Console.WriteLine("--- every announcement ---");
			Console.WriteLine($"  {"capture",-46} {"token",-48} {"amount",10} {"bonus%",7}");
			foreach (var g in grants.OrderBy(g => g.Capture).ThenBy(g => g.Index))
				Console.WriteLine($"  {Trim(g.Capture, 46),-46} {Trim(g.Token, 48),-48} {g.Amount,10} {(g.Bonus?.ToString() ?? "-"),7}");

			Console.WriteLine();
			Console.WriteLine("--- every PAYING kill, with the template words the multiplier reads off ---");
			Console.WriteLine("  ⚠️ the mob's level is off ITS OWN spawn record, never off NpcSpawns (a spawn row is a per-respawn");
			Console.WriteLine("     observation and disagrees with the instance that died); `gap` is mob level minus player level");
			Console.WriteLine($"  {"mob",-32} {"lvl",4} {"gap",4} {"amount",8} {"NpcType",8} {"NpcFlags",10}  {"dist",4}");
			foreach (var k in kills.OrderBy(k => k.MobLevel).ThenBy(k => k.Amount))
			{
				var t = byName.GetValueOrDefault(k.Mob);
				Console.WriteLine($"  {Trim(k.Mob, 32),-32} {k.MobLevel,4} {(int)k.MobLevel - (int)k.PlayerLevel,4} {k.Amount,8} "
				                  + $"{(t.Type?.ToString() ?? "-"),8} {(t.Flags is { } f ? $"0x{f:X}" : "-"),10}  {k.Distance,4}");
			}

			Console.WriteLine();
			Console.WriteLine("--- the ORDINARY (NpcType 4, no flag word) payers, by the mob's level ---");
			foreach (var byLevel in kills.Where(k => k.Bonus is null && byName.GetValueOrDefault(k.Mob) is { Type: 4, Flags: null or 0 })
				         .GroupBy(k => k.MobLevel).OrderBy(g => g.Key))
			{
				var amounts = byLevel.Select(k => k.Amount).Distinct().OrderBy(a => a).ToArray();
				Console.WriteLine($"  level {byLevel.Key,3}  gap {(int)byLevel.First().MobLevel - (int)byLevel.First().PlayerLevel,3}  pays {string.Join("/", amounts),8}  n={byLevel.Count(),2}  "
				                  + string.Join(", ", byLevel.Select(k => k.Mob).Distinct()));
			}

			Console.WriteLine();
			var levels = kills.Where(k => k.Bonus is null && k.Amount > 0).Select(k => k.MobLevel).Distinct().OrderBy(l => l).ToArray();
			Console.WriteLine($"  paying mob levels observed: {string.Join(", ", levels)}");
			Console.WriteLine("  ⚠️ Levels 76 and 77 are UNOBSERVED, which is why the shipped table has two interpolated rows and");
			Console.WriteLine("     the curve is data rather than a formula - see AlternateAdvancementRules.");
			var players = kills.Select(k => k.PlayerLevel).Distinct().OrderBy(l => l).ToArray();
			Console.WriteLine($"  every paying kill was made by a player at level: {string.Join(", ", players)}");
			Console.WriteLine("     ⭐ That is the ACCRUAL GATE, not a sampling artefact: no kill in any levelling capture pays at all,");
			Console.WriteLine("        while a flat item redemption pays a Tortage lowbie 15,000. Kills feed the pool only at the cap.");

			AaWallet(captures);

			Console.WriteLine();
			Console.WriteLine("--- the POOL stat, per capture (1000006 = PvP, 1000007 = PvE) ---");
			Console.WriteLine($"  {"capture",-40} {"stat",8} {"last",10} {"moves",6} {"pushes",7} {"deaths",7} {"pKills",7} {"grants",7} {"push/pk",8}");
			foreach (var s in poolSeries.OrderBy(s => s.Capture).ThenBy(s => s.Stat))
			{
				Console.WriteLine($"  {Trim(s.Capture, 40),-40} {s.Stat,8} {s.Last,10} {s.Moves,6} {s.Pushes,7} {s.Deaths,7} {s.PlayerKills,7} {s.Grants,7} "
				                  + $"{(s.PlayerKills > 0 ? $"{(double)s.Pushes / s.PlayerKills:F2}" : "-"),8}");
			}

			Console.WriteLine();
			Console.WriteLine("  ⚠️ `pushes` outruns `grants`, and NOT at world entry: the first push of every session lands beside a");
			Console.WriteLine("     grant, hundreds to thousands of packets in, so there is no entry restatement to send.");
			Console.WriteLine("  ⭐ The restatements track KILLS rather than grants, at TWO pushes each (#935). `push/pk` reads");
			Console.WriteLine("     exactly 2.00 on six of the nine captures that hold a kill, and a capture with NO kill and one");
			Console.WriteLine("     grant carries exactly 2 pushes - so a grant pays two as well.");
			Console.WriteLine("  ⚠️ It is not yet a rule. The three that miss are 2.20 (10 kills, 5 grants), 2.14 and 0.67, and the");
			Console.WriteLine("     last two are consecutive SLICES of one session - a capture that starts or ends mid-stream loses");
			Console.WriteLine("     the pushes outside its window, and summing the three slices gives 1.85 rather than 2.00, so");
			Console.WriteLine("     clipping does not explain them on its own. We push on a grant alone until it does.");

			AaPushAttribution(pushAttribution);

			Console.WriteLine();
			Console.WriteLine("  ⭐ The pool's DENOMINATOR is the client's own `AlternateAdvancement_XPCap` = 1,000,000, and the");
			Console.WriteLine("     client divides by it itself - see docs/re/alternate-advancement-perks.md.");
		}

		// Every DispatchUpdateTokenAmount retail sent, which is the WALLET half: what a token pool's running totals
		// look like on the wire, and when a pool is restated at all.
		private static void AaWallet(IReadOnlyList<NamedCapture> captures)
		{
			Console.WriteLine();
			Console.WriteLine("--- every DispatchUpdateTokenAmount (0x204D) in the corpus ---");
			Console.WriteLine($"  {"capture",-46} {"idx",6} {"pool",-30} {"gained",8} {"spent",8} {"bonus",6}");
			var seen = 0;
			foreach (var capture in captures)
			{
				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p.WireOpcode != CapturedPacket.Wire(GameCharInterfaceMethods.DispatchUpdateTokenAmount) || p.Body.Length < 18)
						continue;

					UpdateTokenAmount token;
					try
					{
						token = UpdateTokenAmountCodec.Parse(p.Body);
					}
					catch
					{
						continue;
					}

					seen++;
					Console.WriteLine($"  {Trim(capture.Name, 46),-46} {i,6} {token.TokenId,-30} {token.Gained,8} {token.Spent,8} {token.IncludeBonus,6}");
				}
			}

			if (seen == 0)
				Console.WriteLine("  none.");
		}

		/// <summary>
		///     Where each `AAPvEXP` push actually SITS - against the nearest death and the nearest grant.
		/// </summary>
		/// <remarks>
		///     The per-capture ratio `pushes / player kills` reads exactly 2.00 on six of the nine captures that hold a
		///     kill and 2.20 / 2.14 / 0.67 on the rest, which cannot say why: a clipped capture window, a witnessed
		///     death that is not the player's, and a second trigger all produce the same miss. Pairing each push with
		///     the event nearest to it answers it directly, the way the grant announcements were paired to their kills.
		/// </remarks>
		private static void AaPushAttribution(IReadOnlyList<(string Capture, int[] Pushes, (int Index, bool ByPlayer)[] Deaths, int[] Grants)> captures)
		{
			Console.WriteLine();
			Console.WriteLine("--- where each pool push SITS: distance to the nearest death and the nearest grant (#935) ---");

			var toDeath = new List<int>();
			var toGrant = new List<int>();
			int pairedToPlayerKill = 0, pairedToOtherDeath = 0, unpaired = 0, total = 0;
			var perDeath = new List<int>();

			foreach (var c in captures)
			{
				foreach (var push in c.Pushes)
				{
					total++;
					var nearestDeath = c.Deaths.Length == 0 ? (int?)null : c.Deaths.MinBy(d => Math.Abs(d.Index - push)).Index;
					var nearestGrant = c.Grants.Length == 0 ? (int?)null : c.Grants.MinBy(g => Math.Abs(g - push));
					if (nearestDeath is { } nd)
						toDeath.Add(push - nd);
					if (nearestGrant is { } ng)
						toGrant.Add(push - ng);

					var owner = c.Deaths.Where(d => Math.Abs(d.Index - push) <= AaPushToDeathWindow).OrderBy(d => Math.Abs(d.Index - push)).ToArray();
					if (owner.Length == 0)
						unpaired++;
					else if (owner[0].ByPlayer)
						pairedToPlayerKill++;
					else
						pairedToOtherDeath++;
				}

				// How many pushes each death carries, which is the "twice per kill" claim stated per event.
				foreach (var d in c.Deaths)
					perDeath.Add(c.Pushes.Count(p => Math.Abs(p - d.Index) <= AaPushToDeathWindow));
			}

			Console.WriteLine($"  {total} push(es) over {captures.Count} capture(s), window +/-{AaPushToDeathWindow} packets");
			Console.WriteLine($"    paired to a death the PLAYER caused : {pairedToPlayerKill}");
			Console.WriteLine($"    paired to a death they only WITNESSED: {pairedToOtherDeath}");
			Console.WriteLine($"    paired to no death at all           : {unpaired}");
			if (toDeath.Count > 0)
				Console.WriteLine($"  signed distance to the nearest death: {Percentiles(toDeath)}");
			if (toGrant.Count > 0)
				Console.WriteLine($"  signed distance to the nearest grant: {Percentiles(toGrant)}");

			Console.WriteLine();
			Console.WriteLine("  pushes carried by each death (the \"twice per kill\" claim, stated per EVENT):");
			foreach (var g in perDeath.GroupBy(n => n).OrderBy(g => g.Key))
				Console.WriteLine($"    {g.Key} push(es): {g.Count()} death(s)");
		}

		// How near a push has to be to a death to be called its own. Wide enough to cover a burst, narrow enough that
		// two consecutive kills do not both claim the same push - the kills in these captures are seconds apart.
		private const int AaPushToDeathWindow = 30;

		private static string Percentiles(List<int> values)
		{
			values.Sort();

			return $"min {values[0]}, p25 {values[values.Count / 4]}, median {values[values.Count / 2]}, p75 {values[values.Count * 3 / 4]}, max {values[^1]}";
		}

		// A remoteformat parameter's value is `<kind>;<number>` (kind 2 = an integer). Anything else is not a number
		// this report can use, and is skipped rather than guessed at.
		private static long? AaParameterValue(string? raw)
		{
			if (string.IsNullOrEmpty(raw))
				return null;

			var text = raw.Contains(';') ? raw[(raw.IndexOf(';') + 1)..] : raw;

			return long.TryParse(text, out var value) ? value : null;
		}
	}
}
