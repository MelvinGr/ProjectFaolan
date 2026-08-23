using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.Combat;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Per capture, the named NPC's worn equipment (InventoryEntries: slot + template) from its FA015807 (#267).
		// THE COMBAT-BALANCE MEASUREMENT TOOL. CombatResolver's MEASURED constants come from this report, and its
		// class header explains how to read the output; docs/subsystems/combat.md carries the same summary.
		//
		// What retail's combat actually DEALT, by NPC level (#401/#20). The captures carry every floating number as a
		// DamageResultIIR [target][attacker][damage][school], and the spawn extractor gives each NPC handle its level
		// - so joining them measures both directions instead of guessing:
		//   NPC -> player  : the curve ResolveNpcAttackDamage approximates (level * 100, plus the #488 level-gap scale)
		//   player -> NPC  : what a player's hit lands for on a level-N NPC, i.e. the shape any NPC-armour model has
		//                    to reproduce
		// ⚠️ Both sides are POST-mitigation - they are what the client was told to draw - so they are the target
		// numbers, not raw weapon rolls.
		private static void NpcDamageCensus(IReadOnlyList<NamedCapture> captures)
		{
			var scaleRatios = new Dictionary<double, int>();
			var killRatios = new Dictionary<double, int>();
			var hitsToKill = new Dictionary<uint, List<double>>();
			var fromNpc = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var fromNpcSpell = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var toNpcSpell = new Dictionary<(uint Level, DamageSchool School), List<uint>>();
			var toNpc = new Dictionary<(uint Level, DamageSchool School), List<uint>>();

			// Per-ATTACKER melee, the same hits as `fromNpc` but not collapsed into a level bucket (#479). A level
			// bucket mixes archetypes - Poitain carries a 304,944-HP `Poitain Guard` and a 4,605-HP
			// `Zingaran Alchemist` BOTH at level 80 - and the combat log proves how far apart they hit: a
			// `Zingaran Stockpiler` at level 70/72 swings for 7-14 displayed while a `Hired Bodyguard` at 75 swings
			// for 67-92, ~8x at +3 levels. Collapsed by level that reads as a sudden cliff at 74/75 and invites a
			// curve to be fitted through it. `hitsToKill` below already makes this caveat; the melee table did not.
			var fromNpcByAttacker = new Dictionary<(uint Template, uint Level), List<uint>>();
			var attackerHealth = new Dictionary<(uint Template, uint Level), uint>();
			// level -> every captured spawn health at that level (the `hp/curve` denominator, see below).
			var healthByLevel = new Dictionary<uint, List<uint>>();
			var attackerNames = new Dictionary<uint, string>();
			var attackerPlayerLevel = new Dictionary<(uint Template, uint Level), uint>();

			foreach (var c in captures)
			{
				// handle -> level, for every NPC this capture spawned.
				var levels = new Dictionary<uint, uint>();
				var spawnHealth = new Dictionary<uint, uint>();
				var names = new Dictionary<uint, string>();
				var templates = new Dictionary<uint, uint>();
				var playerLevels = new Dictionary<uint, uint>();
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
				{
					var spawnBody = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					if (spawnBody != null && SpawnBodyReader.TryReadCharacterMessage(spawnBody, out var read) && read.Message.Stats is { } st)
					{
						levels[e.Handle] = st.Level;
						if (!string.IsNullOrEmpty(e.Name))
							names[e.Handle] = e.Name;
						templates[e.Handle] = e.TemplateId;
						if (st.Health > 0)
						{
							spawnHealth[e.Handle] = st.Health;
							// The level's health CURVE, from every captured spawn rather than only the ones that hit
							// the player. Built off the attackers alone it was a median of one to three values, so
							// nearly every row read exactly 1.0 - a column that cannot separate an elite from a
							// critter is worse than no column, because it looks like it did (#671).
							(healthByLevel.TryGetValue(st.Level, out var hl) ? hl : healthByLevel[st.Level] = []).Add(st.Health);
						}
					}
				}

				// PLAYER levels, PER HANDLE. NpcSpawnExtractor drops the player's spawn (handle >= the character
				// instance base), which is exactly the record that carries it, so read it straight off the raw spawns.
				// Needed because the interesting variable is not the mob's level but the GAP to whoever it HIT - see
				// the `lvl gap` column below.
				//
				// ⚠️ Kept per handle rather than collapsed to the capture's MAXIMUM, which is what it used to be.
				// A capture is not one player: any level-80 stranger who walks past is spawned into it, and the max
				// then stamped `pc lvl 80` onto every mob in the file. Every one of #671's "elite" outliers is a
				// low-level mob scored against a gap it never fought, so the whole elite question was an artifact of
				// this line (#671).
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					var sb = p.Body;
					if (sb.Length < 20 || sb.ReadU32Be(12) < EntityHandles.CharacterInstanceBase)
						continue;

					// `Body` is already past the packet prologue+header, so it IS the IIR body the reader wants -
					// unlike NpcSpawnEntry.Data above, which is the whole packet and has to be sliced.
					if (SpawnBodyReader.TryReadCharacterMessage(sb, out var pread) && pread.Message.Stats is { Level: > 0 } pst)
						playerLevels[sb.ReadU32Be(12)] = pst.Level;
				}

				// Track each entity's last-seen HealthCurrent from the stat updates, so a hit can be compared with the
				// health drop that follows it - the only way to settle whether the damage number shares the vitals'
				// x100 wire scale or is the raw displayed figure.
				var health = new Dictionary<uint, uint>();
				var pendingHits = new Dictionary<uint, DamageResultIIR>(); // per TARGET: other packets interleave
				var dealtTo = new Dictionary<uint, uint>();             // total damage each NPC took, for the scale check

				foreach (var p in c.Packets)
				{
					if (!p.IsIirCarrier || p.Body is not { Length: >= 8 })
						continue;

					if (p.Body.ReadU32Be(4) == StatUpdateCodec.Opcode && p.Body.Length >= 29)
					{
						var handle = p.Body.ReadU32Be(12);
						var count = p.Body[20];
						for (var k = 0; k < count && 21 + k * 8 + 8 <= p.Body.Length; k++)
						{
							if (p.Body.ReadU32Be(21 + k * 8) != (uint)CharacterStatId.HealthCurrent)
								continue;

							var now = p.Body.ReadU32Be(21 + k * 8 + 4);
							if (pendingHits.Remove(handle, out var prior) && health.TryGetValue(handle, out var before) && before > now)
							{
								var ratio = (double)(before - now) / prior.Damage;
								scaleRatios[Math.Round(ratio, 2)] = scaleRatios.GetValueOrDefault(Math.Round(ratio, 2)) + 1;
							}

							health[handle] = now;
						}

						continue;
					}

					// ⚠️ SPELL damage is NOT a DamageResult - it rides StatDamageIIR (see StatDamageCodec / #349),
					// so a census of DamageResult alone reports "no magical schools" and means only "I looked in the
					// melee packet". A caster NPC's hit is a negative HealthCurrent delta with the caster's handle.
					if (p.Body.ReadU32Be(4) == StatDamageCodec.Opcode)
					{
						StatDamageIIR cast;
						try
						{
							cast = StatDamageCodec.Parse(p.Body.AsSpan(4));
						}
						catch
						{
							continue;
						}

						// ⚠️ The record's "StatId" slot is the damage SCHOOL for a damage record, not a stat id - treating
						// it as one makes Rend reduce stat 1 rather than deal Slashing. So the magical branch IS
						// represented here.
						//
						// The old justification for that ("every observed value is 0..10, i.e. exactly the DamageSchool
						// range") was WRONG on both halves and is what #487 unpicked: the corpus also carries 12
						// (Environmental - fall damage) and 23 (Heal), and the 0..10 numbering itself had Cold in the
						// wrong slot. Fitting inside a range never identified the mapping.
						//
						// Non-damage rows drop out here rather than needing their own filter: Heal is excluded because
						// all 110 of its occurrences carry a non-negative delta, and Environmental because a fall has no
						// caster and its target is a player, so neither NPC branch below claims it.
						if (cast.Delta >= 0)
							continue;

						// Same both-ends rule as the melee tables below: a pet is a spawned entity too, so checking only
						// one end files the PET's own nukes as NPC casts, and a mob's DoT on the pet as damage to the player.
						var magnitude = (uint)(-cast.Delta);
						var casterIsNpc = levels.TryGetValue(cast.Caster, out var casterLevel);
						var spellTargetIsNpc = levels.TryGetValue(cast.Handle, out var spellTargetLevel);

						if (casterIsNpc && !spellTargetIsNpc)
						{
							fromNpcSpell.TryAdd((casterLevel, cast.School), []);
							fromNpcSpell[(casterLevel, cast.School)].Add(magnitude);
						}

						if (spellTargetIsNpc && !casterIsNpc)
						{
							toNpcSpell.TryAdd((spellTargetLevel, cast.School), []);
							toNpcSpell[(spellTargetLevel, cast.School)].Add(magnitude);
						}

						continue;
					}

					if (p.Body.ReadU32Be(4) != DamageResultCodec.Opcode)
						continue;

					DamageResultIIR hit;
					try
					{
						hit = DamageResultCodec.Parse(p.Body.AsSpan(4));
					}
					catch
					{
						continue; // a truncated/odd record - counted by its absence, not by a wrong number
					}

					if (hit.Damage == 0)
						continue;

					pendingHits[hit.Target] = hit;

					// ⚠️ BOTH directions need BOTH ends checked, not just one. `levels` holds every entity the capture
					// spawned - which includes the player's own PETS - so testing only the attacker counted a mob
					// hitting the necro's pet as "NPC -> PLAYER", and testing only the target counted the PET's hits
					// as "PLAYER -> NPC". It showed: `Parasitic Soul` (a necro summon) sat in the NPC->player table
					// as a level-80 attacker dealing 8556. An entity that is not a known spawn is the player.
					var attackerIsNpc = levels.TryGetValue(hit.Attacker, out var attackerLevel);
					var targetIsNpc = levels.TryGetValue(hit.Target, out var targetLevel);

					if (attackerIsNpc && !targetIsNpc)
					{
						fromNpc.TryAdd((attackerLevel, hit.School), []);
						fromNpc[(attackerLevel, hit.School)].Add(hit.Damage);

						// ...and the same hit kept ATTRIBUTED, so the archetype spread stays visible.
						// Keyed by TEMPLATE, never by NAME (#244): ~16 unrelated templates share the internal name
						// "Halen van Dummy", and an elite variant routinely carries the SAME display name as its
						// ordinary cousin - so a name key silently averages two different creatures together.
						if (templates.TryGetValue(hit.Attacker, out var attackerTemplate))
						{
							var key = (attackerTemplate, attackerLevel);
							fromNpcByAttacker.TryAdd(key, []);
							fromNpcByAttacker[key].Add(hit.Damage);
							if (spawnHealth.TryGetValue(hit.Attacker, out var attackerHp))
								attackerHealth[key] = attackerHp;
							if (names.TryGetValue(hit.Attacker, out var attackerName))
								attackerNames[attackerTemplate] = attackerName;
							// The level of the player this hit actually LANDED ON, by victim handle.
							if (playerLevels.TryGetValue(hit.Target, out var victimLevel))
								attackerPlayerLevel[key] = victimLevel;
						}
					}

					// The kill-scale check asks "did this NPC take its own health in damage before it died", so it
					// accumulates damage from ANY source - the player, their pet, a passing guard. Filtering it to
					// player-only hits would undercount every mob a pet helped kill and drag the ratio down.
					if (targetIsNpc)
						dealtTo[hit.Target] = dealtTo.GetValueOrDefault(hit.Target) + hit.Damage;

					if (targetIsNpc && !attackerIsNpc)
					{
						toNpc.TryAdd((targetLevel, hit.School), []);
						toNpc[(targetLevel, hit.School)].Add(hit.Damage);

						// ...and as a FRACTION of what that NPC actually had. A level bucket mixes a 63-health
						// chicken with a 63,000-health elite, so a median damage figure across it means nothing;
						// "what fraction of the target did this hit take" is comparable across both, and its
						// reciprocal is the number a designer actually cares about - hits to kill.
						if (spawnHealth.TryGetValue(hit.Target, out var targetHp) && targetHp > 0)
						{
							var pct = 100.0 * hit.Damage / targetHp;
							hitsToKill.TryAdd(targetLevel, []);
							hitsToKill[targetLevel].Add(pct);
						}
					}
				}

				// An NPC that died took at least its own health in damage; the RATIO of the two says whether the
				// damage number shares the health scale. Only NPCs whose total >= a third of their health are
				// counted, so a mob the player walked away from does not read as "the scale is 3x".
				foreach (var (handle, dealt) in dealtTo)
				{
					if (!spawnHealth.TryGetValue(handle, out var hp) || hp == 0 || dealt * 3 < hp)
						continue;

					var bucket = Math.Round((double)dealt / hp, 2);
					killRatios[bucket] = killRatios.GetValueOrDefault(bucket) + 1;
				}
			}

			Console.WriteLine($"\n=== SCALE: total damage an NPC took vs the health its SPAWN declared ===");
			Console.WriteLine($"{"ratio",8} {"npcs",6}   (~1 = same scale as health; ~0.01 = damage is x100 of health)");
			foreach (var (bucket, n) in killRatios.OrderByDescending(k => k.Value).Take(8))
				Console.WriteLine($"{bucket,8:F2} {n,6}");

			Console.WriteLine($"\n=== SCALE: does a hit move the target's health by the SAME number? ===");
			Console.WriteLine($"{"ratio",8} {"pairs",6}   (1.00 = damage and health share a scale; 0.01 = damage is x100 of health)");
			foreach (var (ratio, n) in scaleRatios.OrderByDescending(k => k.Value).Take(6))
				Console.WriteLine($"{ratio,8:F2} {n,6}");

			// The model is the SERVER's, called - not a copy of it. This column used to hardcode `5 + level * 3`, a
			// formula the server had already replaced with `level * 100`, so it spent that time reporting our fit
			// against a curve nothing ran (#479).
			// `level, level` = a player at the mob's own level, i.e. the un-penalised case - the level-gap penalty
			// (#488) is a separate factor and the column below is the one that shows it.
			// ⚠️ WIRE units, not the resolver's display return. Captured damage is a wire number, and comparing it
			// to the display value multiplied every `x model` in this report by 100 from #572 until #671 caught it.
			Report("NPC -> PLAYER (what ResolveNpcAttackDamage should reproduce)", fromNpc, level => CombatResolver.NpcAttackDamageWire(level, level));

			// Per attacker, so the archetype spread is visible instead of averaged away. `x model` is the measured
			// median over what the server would deal at that level: ~1.0 means the level curve fits this attacker.
			Console.WriteLine($"\n=== NPC -> PLAYER, BY ATTACKER (the level buckets above mix archetypes) ===");
			Console.WriteLine("⚠️ `x model` is scored in WIRE units on both sides. `modes` splits an attacker's hits at their");
			Console.WriteLine("   largest RATIO gap when that gap is >= 3x with 2+ hits either side - a median taken across two");
			Console.WriteLine("   mechanics (an auto-swing and a special) describes neither, which is what #671 stalled on.");
			Console.WriteLine($"{"level",5} {"tpl",8} {"attacker",-24} {"hits",5} {"min",7} {"median",7} {"max",7} {"ours",7} {"x model",8} {"pc lvl",7} {"lvl gap",8} {"hp/curve",9} {"hit%hp",7}  modes");
			foreach (var ((template, level), hits) in fromNpcByAttacker.OrderBy(k => k.Key.Level).ThenBy(k => k.Key.Template))
			{
				var sorted = hits.Order().ToList();
				var median = sorted[sorted.Count / 2];
				var pcLvl = attackerPlayerLevel.GetValueOrDefault((template, level));
				// Scored against what the server would ACTUALLY deal to that player - the level-gap penalty included
				// (#488) - so `x model` reads 1.0 when we reproduce the fight rather than only when the gap is 0. Rows
				// with no resolvable player level fall back to the un-penalised curve.
				var ours = CombatResolver.NpcAttackDamageWire(level, pcLvl > 0 ? pcLvl : level);

				var hp = attackerHealth.GetValueOrDefault((template, level));
				var levelHp = healthByLevel.TryGetValue(level, out var pool) && pool.Count > 0 ? pool.Order().ToList()[pool.Count / 2] : 0;
				var hpRatio = hp > 0 && levelHp > 0 ? (double)hp / levelHp : 0;

				Console.WriteLine($"{level,5} {template,8} {Trunc(attackerNames.GetValueOrDefault(template, "?"), 24),-24} {sorted.Count,5} {sorted[0],7} {median,7} {sorted[^1],7} {ours,7} {(ours > 0 ? (double)median / ours : 0),8:F2} {(pcLvl > 0 ? pcLvl.ToString() : "-"),7} {(pcLvl > 0 ? ((int)pcLvl - (int)level).ToString() : "-"),8} {(hpRatio > 0 ? hpRatio.ToString("F1") : "-"),9} {(hp > 0 ? (100.0 * median / hp).ToString("F2") : "-"),7}  {Modes(sorted, ours)}");
			}

			// ⭐ THE HEAD-TO-HEAD (#671). The ticket asked for an ELITE multiplier on top of the level curve. The
			// better question is whether LEVEL is the right base at all: we also hold the attacker's own maximum
			// health (`NpcSpawn.Health`), and a swing that is a fixed share of the swinger's pool would explain the
			// "elites hit harder" observation without an archetype column, because an elite's pool is what makes it
			// an elite.
			//
			// Scored only where the model is not being asked to do the level-gap's job too - |gap| <= 2 - and only
			// on rows with enough hits to have a median worth the name. The statistic is the SPREAD: a predictor
			// whose ratio clusters is the one that predicts.
			var comparable = fromNpcByAttacker
				.Where(kv => kv.Value.Count >= 8 && attackerHealth.ContainsKey(kv.Key) && attackerPlayerLevel.TryGetValue(kv.Key, out var pc) && Math.Abs((int)pc - (int)kv.Key.Level) <= 2)
				.Select(kv =>
				{
					var sorted = kv.Value.Order().ToList();
					var median = sorted[sorted.Count / 2];
					var model = CombatResolver.NpcAttackDamageWire(kv.Key.Level, attackerPlayerLevel[kv.Key]);
					return (Name: attackerNames.GetValueOrDefault(kv.Key.Template, "?"), kv.Key.Level, sorted.Count,
						ByLevel: model > 0 ? (double)median / model : 0,
						ByHealth: 100.0 * median / attackerHealth[kv.Key]);
				})
				.OrderBy(r => r.Level)
				.ToList();

			Console.WriteLine();
			Console.WriteLine($"=== NPC -> PLAYER: is LEVEL or the attacker's own HEALTH the better base? ({comparable.Count} rows, |gap| <= 2, >= 8 hits) ===");
			Console.WriteLine($"{"level",5} {"attacker",-26} {"hits",5} {"x level model",14} {"% of own hp",12}");
			foreach (var r in comparable)
				Console.WriteLine($"{r.Level,5} {Trunc(r.Name, 26),-26} {r.Count,5} {r.ByLevel,14:F2} {r.ByHealth,12:F2}");

			if (comparable.Count >= 3)
			{
				Console.WriteLine($"  by LEVEL : min {comparable.Min(r => r.ByLevel):F2} max {comparable.Max(r => r.ByLevel):F2}"
				                  + $" -> spread {comparable.Max(r => r.ByLevel) / Math.Max(0.0001, comparable.Min(r => r.ByLevel)):F1}x");
				Console.WriteLine($"  by HEALTH: min {comparable.Min(r => r.ByHealth):F2}% max {comparable.Max(r => r.ByHealth):F2}%"
				                  + $" -> spread {comparable.Max(r => r.ByHealth) / Math.Max(0.0001, comparable.Min(r => r.ByHealth)):F1}x"
				                  + $", median {comparable.Select(r => r.ByHealth).Order().ToList()[comparable.Count / 2]:F2}%");
			}

			static string Trunc(string s, int n)
			{
				return s.Length <= n ? s : s[..n];
			}

			// Splits an attacker's hits at the largest RATIO gap between consecutive sorted values, when that gap is
			// wide enough and both sides are populated. A per-archetype damage multiplier cannot express two
			// mechanics in one number, so before fitting one the question is whether there IS one number: `Vanir
			// Sentinel` L26's 25 hits run 127 / 4191 / 5217, where 127 is what the level-gap model predicts and 5217
			// is 40x that. One template, one level, one player, one fight - and two mechanics.
			static string Modes(List<uint> sorted, uint model)
			{
				const double splitRatio = 3.0;
				const int minSide = 2;
				if (sorted.Count < 2 * minSide)
					return "-";

				var at = -1;
				var best = splitRatio;
				for (var i = minSide - 1; i <= sorted.Count - minSide - 1; i++)
				{
					if (sorted[i] == 0)
						continue;

					var ratio = (double)sorted[i + 1] / sorted[i];
					if (ratio <= best)
						continue;

					best = ratio;
					at = i;
				}

				if (at < 0)
					return "one";

				var low = sorted[..(at + 1)];
				var high = sorted[(at + 1)..];
				return $"TWO: {low.Count}x median {low[low.Count / 2]} ({Times(low[low.Count / 2], model)})"
				       + $" | {high.Count}x median {high[high.Count / 2]} ({Times(high[high.Count / 2], model)}), {best:F1}x apart";
			}

			static string Times(uint value, uint model)
			{
				return model > 0 ? $"{(double)value / model:F2}x" : "-";
			}
			Report("PLAYER -> NPC (the shape an NPC-armour model has to reproduce)", toNpc, null);
			Console.WriteLine($"\n=== PLAYER -> NPC as a FRACTION of the target's own health ===");
			Console.WriteLine($"{"level",5} {"hits",6} {"median %",9} {"hits-to-kill",13}");
			foreach (var (level, pcts) in hitsToKill.OrderBy(k => k.Key))
			{
				var sorted = pcts.Order().ToList();
				var median = sorted[sorted.Count / 2];
				Console.WriteLine($"{level,5} {sorted.Count,6} {median,9:F1} {(median > 0 ? 100.0 / median : 0),13:F1}");
			}

			Report("NPC -> PLAYER, SPELL/DoT damage (StatDamageIIR)", fromNpcSpell, null);
			Report("PLAYER -> NPC, SPELL/DoT damage (StatDamageIIR)", toNpcSpell, null);

			// The school decides which mitigation BRANCH the client runs (FUN_0113ba00 switches on it): 1/2/3/8 take
			// the physical branch - armour, Strength-scaled - and everything else the magical one, protection scaled
			// by Int/Wis. Mixing them into one average would compare a sword swing with a fire spell.
			static void Report(string title, Dictionary<(uint Level, DamageSchool School), List<uint>> byLevel, Func<uint, uint>? model)
			{
				Console.WriteLine($"\n=== {title} ===");
				Console.WriteLine($"{"level",5} {"school",-12} {"branch",-9} {"hits",6} {"min",6} {"median",7} {"mean",7} {"max",6}" + (model == null ? "" : $" {"ours",6}"));
				foreach (var ((level, school), hits) in byLevel.OrderBy(k => k.Key.Level).ThenBy(k => k.Key.School))
				{
					var sorted = hits.Order().ToList();
					var branch = school is DamageSchool.Slashing or DamageSchool.Piercing or DamageSchool.Crushing or DamageSchool.Poison
						? "physical"
						: "magical";
					var line = $"{level,5} {school,-12} {branch,-9} {sorted.Count,6} {sorted[0],6} {sorted[sorted.Count / 2],7} {sorted.Select(v => (double)v).Average(),7:F1} {sorted[^1],6}";
					Console.WriteLine(model == null ? line : line + $" {model(level),6}");
				}
			}
		}

		// --report dot-trains [--report-arg <spell name substring or base id>]
		//
		// The EVIDENCE behind every measured DoT tick interval: each `(spell, target, caster)` tick train the corpus
		// contains, how long it is, and the gaps it contributes - the exact input `DotIntervalEstimator` is handed.
		//
		// It exists because #524's open item is a spell whose gaps cluster on a LATTICE (Aura: Fire Weave, 17 gaps at
		// 0.92 / 1.23 / 1.53 / 1.84, spaced ~0.3s), and there are two very different explanations that the pooled gap
		// list cannot tell apart:
		//   * a HARMONIC - one long train of a ~0.3s pulse whose ticks are mostly unobserved, so every surviving gap
		//     is a multiple of the fundamental (the estimator's documented blind spot);
		//   * RE-APPLICATION phase - many SHORT trains, each contributing one arbitrary first-gap, in which case the
		//     lattice is an artefact of when the aura was recast and the spell simply has no measurable interval.
		// Train LENGTH separates them, and only this report shows it.
		private static void DotTrains(IReadOnlyList<NamedCapture> captures, string? filter, string? outPath)
		{
			var wanted = (filter ?? "").Trim();
			var trainLengths = new Dictionary<uint, List<int>>();
			var gapsBySpell = new Dictionary<uint, List<double>>();
			var names = new Dictionary<uint, string>();

			foreach (var capture in captures)
			{
				// Same key as CaptureStatDamageImporter: a train is one spell on one target from one caster, within
				// ONE capture (handles are session-volatile).
				var trains = new Dictionary<(uint Spell, uint Target, uint Caster), List<long>>();
				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: StatDamageCodec.Opcode })
						continue;

					if (!p.TryParse(StatDamageCodec.Parse, out var dmg) || dmg.SpellBaseId == 0 || dmg.Delta == 0)
						continue;

					var key = (dmg.SpellBaseId, dmg.Handle, dmg.Caster);
					trains.TryAdd(key, []);
					trains[key].Add(p.Ticks);
				}

				foreach (var (key, times) in trains)
				{
					times.Sort();
					trainLengths.TryAdd(key.Spell, []);
					trainLengths[key.Spell].Add(times.Count);
					if (times.Count < 2)
						continue;

					gapsBySpell.TryAdd(key.Spell, []);
					for (var i = 1; i < times.Count; i++)
						gapsBySpell[key.Spell].Add((times[i] - times[i - 1]) / 1e7);
				}
			}

			using var db = OpenDb();
			var ids = trainLengths.Keys.ToList();
			foreach (var spell in db.Spells.Where(s => ids.Contains(s.Id)))
				names[spell.Id] = spell.Name;

			Console.WriteLine($"{"spell",-42} {"trains",6} {"ticks",6} {"len1",5} {"len2",5} {"len3+",5} {"gaps",5}  estimate");
			foreach (var (spellId, lengths) in trainLengths.OrderByDescending(kv => kv.Value.Sum()))
			{
				var name = names.GetValueOrDefault(spellId, $"(uncatalogued {spellId})");
				if (wanted.Length > 0 && !name.Contains(wanted, StringComparison.OrdinalIgnoreCase) && spellId.ToString() != wanted)
					continue;

				var gaps = gapsBySpell.GetValueOrDefault(spellId, []);
				var estimate = DotIntervalEstimator.Estimate(gaps);
				Console.WriteLine($"{name[..Math.Min(42, name.Length)],-42} {lengths.Count,6} {lengths.Sum(),6} "
				                  + $"{lengths.Count(l => l == 1),5} {lengths.Count(l => l == 2),5} {lengths.Count(l => l >= 3),5} {gaps.Count,5}"
				                  + $"  {(estimate is { } ms ? $"{ms} ms" : "(none)")}");

				// The whole point when drilling one spell: the gaps WITH the train that produced each.
				if (wanted.Length == 0)
					continue;

				Console.WriteLine($"      train lengths: {string.Join(" ", lengths.OrderByDescending(l => l))}");
				Console.WriteLine($"      gaps: {string.Join(" ", gaps.OrderBy(g => g).Select(g => $"{g:0.00}"))}");
			}

			Console.WriteLine();
			Console.WriteLine("A train of length 2 contributes exactly ONE gap, and that gap is a re-application phase offset as often "
			                  + "as it is the period. A spell whose gaps come mostly from length-2 trains has no measured cadence, whatever "
			                  + "the pooled gaps look like (#524).");

			if (outPath == null)
				return;

			// `--out <file>` writes the SAME input at full precision, one (spell, gap) row per line. The console view
			// rounds to 2 dp, which is enough to read a cluster and not enough to re-run a candidate acceptance rule
			// against - and re-running one over every spell at once is the gate #657 asks any new rule to pass.
			var rows = new List<string> { "spellId\tname\tgapSeconds" };
			foreach (var (spellId, gaps) in gapsBySpell.OrderBy(kv => kv.Key))
			foreach (var gap in gaps.OrderBy(g => g))
				rows.Add($"{spellId}\t{names.GetValueOrDefault(spellId, "")}\t{gap:R}");

			File.WriteAllLines(outPath, rows);
			Console.WriteLine($"wrote {outPath} ({rows.Count - 1} gaps)");
		}

		// --report chain-kill
		//
		// Every `ChainKillIIR` in the corpus in its own timeline, against the kill counters that move with it: stat
		// `0x6A` (106) and `0xF4256` (1000070), which #446 read as "remaining of 20" and "achieved". Prints each
		// counter move and each ChainKill, with the seconds since the previous one of each.
		//
		// #446's open questions are all TIMING questions - "what exactly triggers the IIR: N kills within a time
		// window? Both arrivals are ~9-17s apart, so a window is plausible but unmeasured" - and it proposed a fresh
		// retail session to settle them. The corpus already carries the records; this reads them.
		//
		// ⚠️ It does NOT need the IIR body decoded, and the body is not the gap: `ChainKillCodec` already reads it
		// byte-exact off the client's own reader (handle, env, count). What is missing is when to SEND one.
		private static void ChainKill(IReadOnlyList<NamedCapture> captures)
		{
			const uint chainCounterA = 0x6A; // 106 - "remaining of 20" in #446's reading
			const uint chainCounterB = 0xF4256; // 1000070 - DailyPvPKills per the client's stat-name table
			const uint chainCounterC = 0xF4255; // 1000069 - DailyPvEKills, its sibling

			var counts = new List<uint>();
			var gaps = new List<double>();
			var resetDelays = new List<double>();   // non-zero -> the 0 that closes it
			var escalations = new List<double>();   // non-zero -> a HIGHER non-zero
			var withACounterMove = 0;
			foreach (var capture in captures)
			{
				var lines = new List<string>();
				long? previousChain = null;
				var previousCount = 0u;
				long? previousCounter = null;
				var counterMovesSinceChain = 0;
				var recent = new List<(long Ticks, uint Type)>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					recent.Add((p.Ticks, p.SubOpcode));
					if (recent.Count > 400)
						recent.RemoveRange(0, 200);

					if (p.SubOpcode == StatUpdateCodec.Opcode)
					{
						if (!p.TryParse(StatUpdateCodec.Parse, out var update))
							continue;

						var moved = update.Stats.Records
							.Where(r => (uint)r.StatId is chainCounterA or chainCounterB or chainCounterC)
							.ToList();
						if (moved.Count == 0)
							continue;

						counterMovesSinceChain++;
						var since = previousCounter is { } pc ? $"{TimeSpan.FromTicks(p.Ticks - pc).TotalSeconds,6:0.0}s" : "     -";
						previousCounter = p.Ticks;
						lines.Add($"    counter  {since} since previous   0x{update.EntityHandle:X8}  "
						          + string.Join("  ", moved.Select(r => $"{(uint)r.StatId}={r.RawValue}")));
						continue;
					}

					if (p.SubOpcode != ChainKillCodec.Opcode || !p.TryParse(ChainKillCodec.Parse, out var chain))
						continue;

					var gap = previousChain is { } pk ? TimeSpan.FromTicks(p.Ticks - pk).TotalSeconds : double.NaN;
					if (!double.IsNaN(gap))
					{
						gaps.Add(gap);
						if (chain.Count == 0 && previousCount > 0)
							resetDelays.Add(gap);
						else if (chain.Count > previousCount && previousCount > 0)
							escalations.Add(gap);
					}

					counts.Add(chain.Count);
					if (counterMovesSinceChain > 0)
						withACounterMove++;

					// What arrived in the two seconds before it - the trigger names itself if anything does.
					var before = recent
						.Where(r => TimeSpan.FromTicks(p.Ticks - r.Ticks).TotalSeconds <= 2.0)
						.GroupBy(r => r.Type)
						.OrderByDescending(g => g.Count())
						.Select(g => $"{IirName(g.Key)}x{g.Count()}")
						.Take(8);

					lines.Add($"  CHAINKILL {(double.IsNaN(gap) ? "     -" : $"{gap,6:0.0}s")} since previous   0x{chain.Handle:X8}  "
					          + $"count={chain.Count}  env=0x{chain.Env:X2}  ({counterMovesSinceChain} counter move(s) since the last chain)");
					lines.Add($"              preceded within 2s by: {string.Join(" ", before)}");
					previousChain = p.Ticks;
					previousCount = chain.Count;
					counterMovesSinceChain = 0;
				}

				if (!lines.Exists(l => l.Contains("CHAINKILL", StringComparison.Ordinal)))
					continue;

				Console.WriteLine(capture.Name);
				foreach (var line in lines)
					Console.WriteLine(line);
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL: {counts.Count} ChainKillIIR record(s) over {captures.Count} captures.");
			if (counts.Count > 0)
			{
				Console.WriteLine("  count values: " + string.Join(", ", counts.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}")));
				Console.WriteLine($"  arrivals preceded by a kill-counter move: {withACounterMove} of {counts.Count}");
			}

			if (gaps.Count > 0)
			{
				var sorted = gaps.OrderBy(g => g).ToList();
				Console.WriteLine($"  gap between consecutive chains: min={sorted[0]:0.0}s median={sorted[sorted.Count / 2]:0.0}s max={sorted[^1]:0.0}s");
			}

			Describe("reset  (non-zero -> 0)", resetDelays);
			Describe("escalate (n -> n+1)", escalations);
			return;

			static void Describe(string what, List<double> values)
			{
				if (values.Count == 0)
					return;

				var sorted = values.OrderBy(v => v).ToList();
				Console.WriteLine($"  {what}: {sorted.Count} sample(s), min={sorted[0]:0.00}s median={sorted[sorted.Count / 2]:0.00}s max={sorted[^1]:0.00}s");
			}

			static string IirName(uint type)
			{
				return Enum.IsDefined(typeof(IirTypes), type) ? ((IirTypes)type).ToString() : $"0x{type:X8}";
			}
		}
	}
}
