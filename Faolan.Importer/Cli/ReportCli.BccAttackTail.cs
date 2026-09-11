using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report bcc-attack-tail`: the middle block of every captured attack tail, word by word.
	//
	// The tail is `[targets][middle][results]`, which accounts for every captured body. The client's reader is what
	// says where each word goes
	// (`FUN_0084d5f0` then `FUN_01151170`, the shared Combat:Melee /:Unarmed reader; Ranged wraps it), so this
	// report prints each word under the Command-object slot the client stores it in, and its whole value
	// distribution beside it. A slot plus a distribution is what turns "eight opaque words" into a field.
	//
	// It reads the tail with the same arithmetic BehaviorAcks documents, so a change to that grammar breaks this
	// report rather than quietly making it print nonsense.
	public static partial class ReportCli
	{
		// Where the client's reader puts each middle word, in wire order. `FUN_0084d5f0` reads the target list and
		// then words 0-1 (and word 1b when the selector is > 1); `FUN_01151170` reads the remaining six and then the
		// result count. Names are the ones the RE established; a slot with no established meaning is named for its
		// offset, deliberately, so nothing here reads as decoded that is not.
		private static readonly string[] MiddleSlots =
		[
			"Command+0x164",
			"Command+0x144  SELECTOR (>1 adds a word)",
			"flags -> +0x1A0 (bit4) / +0x1A1 (bit5) / +0x198 (bit6)",
			"Command+0x19C",
			"Command+0x44",
			"Command+0xD0",
			"Command+0x188 \\ read back as ONE pair",
			"Command+0x18C /"
		];

		// The client's own five-zone direction alphabet, in its order. Recovered from the exe, not assigned:
		//   * `FUN_005e3b30` is `switch (dir) { 1 -> 0xC9, 2 -> 0xCA, default -> 0xCB, 4 -> 0xCC, 5 -> 0xCD }` over an
		//     internal 1..5 enum (`10` means "random": `rand() % 6 + 1`, so overhead - case 3 and 6 - draws double);
		//   * the config keys it registers are `attack_dir_bm_upper_left`, `_upper_right`, `_head`, `_lower_left`,
		//     `_lower_right`, in that block order (`FUN_0152f740`), which is the same 1..5;
		//   * `FUN_005edd90`, the combo step-list parser, rewrites `0xCC -> 0xC9` and `0xCD -> 0xCA` below level 40 -
		//     the lower zones folding onto the UPPER one on the same SIDE, which pins the two sides independently.
		// The first three are pinned to `Abilities` rows by the corpus with zero exceptions.
		private static readonly (uint Code, string Name, string Arc)[] Directions =
		[
			(0xC9, "Upper Left", "Left"),
			(0xCA, "Upper Right", "Right"),
			(0xCB, "Overhead", "Mid"),
			(0xCC, "Lower Left", "Left"),
			(0xCD, "Lower Right", "Right")
		];

		private static readonly HashSet<uint> DirectionCodes = Directions.Select(d => d.Code).ToHashSet();

		private static void BccAttackTail(IReadOnlyList<NamedCapture> captures)
		{
			var attacks = new[] { BehaviorId.CombatMelee, BehaviorId.CombatRanged, BehaviorId.CombatUnarmed }.Select(b => (uint)b).ToHashSet();

			// Per middle-word position: value -> sightings. Position 8 is the EXTRA word the selector adds, kept
			// separate from the eight so the common form's distributions stay clean.
			var words = new Dictionary<uint, int>[MiddleSlots.Length + 1];
			for (var i = 0; i < words.Length; i++)
				words[i] = [];

			var tails = 0;
			var extended = 0;
			var unexplained = 0;
			// The rows where a middle word is non-zero, so the report can show what such a hit looked like rather
			// than only that one exists.
			var interesting = new List<(string Capture, int Frame, uint[] Middle, int Targets, int Results)>();
			var joint = new Dictionary<(uint W4, uint W5), int>();
			// word 2's flag bits are the only middle slot left that could carry a per-hit outcome - a crit, a
			// block, an evade - which is what that ticket says the block is worth decoding for. Cross-tab it against
			// everything the tail already knows about the swing, because a flag that is a function of one of those is
			// not an outcome at all.
			var word2Versus = new Dictionary<(string Field, string Value, uint Flags), int>();
			var versusResult = new Dictionary<(uint W4, uint ResultType), int>();
			var versusDirection = new Dictionary<(uint W5, uint ResultType), int>();
			// An NPC's swing carries no direction ability: word 5 is an ability id and a mob has none. The open
			// question is whether the per-victim result type - which is the same direction - is present on a mob's
			// swing too, so the tally is split by actor.
			var byActor = new Dictionary<(bool Player, string Field, uint Value), int>();
			// The mob alphabet is five codes where our rose has three arcs. Two questions the actor split cannot
			// answer - does the five-code alphabet collapse onto the three arcs uniformly, and is the five-vs-three
			// split a property of the template (some mobs swinging only the three a low-level player has) rather than
			// of every mob. Keyed by template, never by name.
			var byTemplate = new Dictionary<uint, Dictionary<uint, int>>();
			var templateNames = new Dictionary<uint, string>();
			// A player swing that names no direction ability is a combo step or a spell. It still carries a result
			// type, and whether that type is a spread or a constant decides whether our ack may keep writing 0xCB on
			// the undirected path or has to source the direction some other way (the combo catalog states one).
			var playerUndirected = new Dictionary<(bool Directed, uint ResultType), int>();

			var perActorWord4 = new Dictionary<(string, uint), List<uint>>();
			var perTargetWord4 = new Dictionary<(string, uint), List<uint>>();
			var perActorWord5 = new Dictionary<(string, uint), List<uint>>();
			var perTargetWord5 = new Dictionary<(string, uint), List<uint>>();

			foreach (var c in captures)
			{
				// Who is an NPC, per capture. A retail character id is ~0x0131xxxx, i.e. below
				// EntityHandles.CharacterInstanceBase, so the handle number line cannot answer this on a capture -
				// the only reliable test is the spawn itself, and `player_stats` is what distinguishes the two.
				var npcHandles = new HashSet<uint>();
				var npcTemplates = new Dictionary<uint, uint>();
				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;
					if (!p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn) || spawn.Character?.player_stats != null)
						continue;

					npcHandles.Add(spawn.Handle);
					var template = spawn.Character?.npc_stats?.TemplateId ?? 0;
					if (template == 0)
						continue;

					npcTemplates[spawn.Handle] = template;
					if (!string.IsNullOrEmpty(spawn.Character?.Name))
						templateNames[template] = spawn.Character!.Name!;
				}

			foreach (var p in c.Packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;
				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || !attacks.Contains(bcc.BehaviorId))
					continue;

				if (!TryReadAttackTail(bcc, out var middle, out var targets, out var results, out var resultTypes, out var targetHandles))
				{
					unexplained++;
					continue;
				}

				tails++;
				if (middle.Length > MiddleSlots.Length)
					extended++;

				for (var i = 0; i < middle.Length; i++)
				{
					// The extra word sits at wire position 2 but is reported last, so the eight fixed slots keep one
					// meaning each whatever form the tail took.
					var slot = middle.Length > MiddleSlots.Length && i == 2 ? MiddleSlots.Length : i > 2 && middle.Length > MiddleSlots.Length ? i - 1 : i;
					words[slot][middle[i]] = words[slot].GetValueOrDefault(middle[i]) + 1;
				}

				var key = (middle[^4], middle[^3]);
				joint[key] = joint.GetValueOrDefault(key) + 1;
				var playerActor = !npcHandles.Contains(bcc.Actor);
				Count(playerActor, "word5 direction", middle[^3]);
				npcTemplates.TryGetValue(bcc.Actor, out var actorTemplate);
				foreach (var rt in resultTypes)
				{
					Count(playerActor, "result type", rt);
					if (playerActor)
					{
						var pkey = (middle[^3] != 0, rt);
						playerUndirected[pkey] = playerUndirected.GetValueOrDefault(pkey) + 1;
					}

					if (!playerActor && actorTemplate != 0 && DirectionCodes.Contains(rt))
					{
						var perTemplate = byTemplate.TryGetValue(actorTemplate, out var d2) ? d2 : byTemplate[actorTemplate] = [];
						perTemplate[rt] = perTemplate.GetValueOrDefault(rt) + 1;
					}

					var rkey = (middle[^4], rt);
					versusResult[rkey] = versusResult.GetValueOrDefault(rkey) + 1;
					var dkey = (middle[^3], rt);
					versusDirection[dkey] = versusDirection.GetValueOrDefault(dkey) + 1;
				}

				// word 2 against the swing's own shape. `middle[2]` is the flags word in both forms - the extra
				// word the selector inserts sits at wire position 2 and pushes the rest along, so read it from the end
				// like the word4/word5 keys above do.
				var flags = middle[^6];
				void W2(string field, string value)
				{
					var k = (field, value, flags);
					word2Versus[k] = word2Versus.GetValueOrDefault(k) + 1;
				}

				W2("actor", playerActor ? "player" : "npc");
				W2("results", results == 0 ? "none" : results == 1 ? "one" : "many");
				W2("targets", targets == 0 ? "none" : targets == 1 ? "one" : "many");
				W2("charge", middle[^8] == 0 ? "zero" : "non-zero");
				W2("direction", middle[^3] == 0 ? "absent" : "present");
				foreach (var rt in resultTypes)
					W2("result type", $"0x{rt:X2}");

				// Word 4: whose property is it? The scope test is to group the value by attacker and by defender and
				// see which grouping it is stable within. A defender's guard arc is stable per defender; a second
				// attacker property is stable per attacker.
				if (middle[^4] != 0)
				{
					var w4 = middle[^4];
					perActorWord4.TryAdd((c.Name, bcc.Actor), []);
					perActorWord4[(c.Name, bcc.Actor)].Add(w4);
					foreach (var target in targetHandles)
					{
						perTargetWord4.TryAdd((c.Name, target), []);
						perTargetWord4[(c.Name, target)].Add(w4);
					}

					// ...and the same for word 5, the known attacker-side direction, as the control. A test that
					// cannot tell word 5 apart is measuring nothing.
					perActorWord5.TryAdd((c.Name, bcc.Actor), []);
					perActorWord5[(c.Name, bcc.Actor)].Add(middle[^3]);
					foreach (var target in targetHandles)
					{
						perTargetWord5.TryAdd((c.Name, target), []);
						perTargetWord5[(c.Name, target)].Add(middle[^3]);
					}
				}

				if (middle.Skip(2).Any(w => w != 0))
					interesting.Add((c.Name, p.Frame, middle, targets, results));
			}
			}

			Console.WriteLine();
			// measured and INAPPLICABLE - printed so the next attempt does not rebuild it. The scope test
			// that settled the MagicBuff payload's two level slots (whose field is it? group by each candidate owner
			// and see which grouping it is stable within) does not work on the attack tail, and the control is what
			// says so: word 5 is a known attacker-side direction, and it comes out just as unstable per attacker as
			// per defender. Both words are rolled per swing, so neither is a property of an entity at all - and the
			// populations (19 attackers / 18 defenders) are far too small either way.
			Console.WriteLine("word 4 SCOPE (#661) - is it the ATTACKER's or the DEFENDER's? A value is 'unstable' in a");
			Console.WriteLine("grouping when that entity was seen with more than one of them. Word 5, the known");
			Console.WriteLine("attacker-side direction, is the CONTROL - and it fails, so this test cannot answer word 4:");
			Console.WriteLine($"{"word",6} {"per ATTACKER",28} {"per DEFENDER",28}");
			foreach (var (label, scopedByActor, scopedByTarget) in new[]
			         {
				         ("4", perActorWord4, perTargetWord4),
				         ("5 (control)", perActorWord5, perTargetWord5)
			         })
			{
				var au = scopedByActor.Count(g => g.Value.Distinct().Count() > 1);
				var tu = scopedByTarget.Count(g => g.Value.Distinct().Count() > 1);
				Console.WriteLine($"{label,6} {$"{au} of {scopedByActor.Count} unstable ({(scopedByActor.Count == 0 ? 0 : 100.0 * au / scopedByActor.Count):0.0}%)",28} "
				                  + $"{$"{tu} of {scopedByTarget.Count} unstable ({(scopedByTarget.Count == 0 ? 0 : 100.0 * tu / scopedByTarget.Count):0.0}%)",28}");
			}

			Console.WriteLine();
			Console.WriteLine("word 2 (the flag bits) against everything else the tail states (#661):");
			foreach (var field in word2Versus.Keys.Select(k => k.Field).Distinct())
			{
				Console.WriteLine($"  by {field}:");
				foreach (var g in word2Versus.Where(kv => kv.Key.Field == field).GroupBy(kv => kv.Key.Value).OrderBy(g => g.Key))
				{
					var total = g.Sum(kv => kv.Value);
					var parts = g.OrderByDescending(kv => kv.Value).Select(kv => $"0x{kv.Key.Flags:X2}={kv.Value}");
					Console.WriteLine($"    {g.Key,-14} n={total,-6} {string.Join("  ", parts)}");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"{tails} s2c attack tails (Combat:Melee / :Ranged / :Unarmed) across {captures.Count} captures");
			Console.WriteLine($"{extended} carry the NINE-word form (selector > 1); {unexplained} did not fit the grammar at all");
			Console.WriteLine();
			Console.WriteLine("word  client slot                                              distinct  values (top 8)");

			for (var i = 0; i < words.Length; i++)
			{
				var slot = i < MiddleSlots.Length ? MiddleSlots[i] : "Command+0x148  (only when the selector is > 1)";
				var d = words[i];
				if (d.Count == 0)
				{
					Console.WriteLine($"{Label(i),4}  {slot,-55}         -  (never present)");
					continue;
				}

				var top = string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{Describe(kv.Key)} x{kv.Value}"));
				Console.WriteLine($"{Label(i),4}  {slot,-55}  {d.Count,8}  {top}");
			}

			// The three words that turned out to be ENUMS take a handful of values each, so print the whole set and
			// resolve what the ids are: word 5 is an Abilities row (the swing direction), which is the finding this
			// report exists to make re-derivable.
			using var db = OpenDb();
			var abilities = db.Abilities.AsNoTracking().ToDictionary(a => a.Id, a => a.Name);
			Console.WriteLine();
			Console.WriteLine("the small-alphabet words in full:");
			foreach (var i in new[] { 2, 4, 5, 6, 7 })
			{
				Console.WriteLine($"  word {i}  {MiddleSlots[i]}");
				foreach (var (value, n) in words[i].OrderByDescending(kv => kv.Value))
				{
					var named = abilities.TryGetValue(value, out var name) ? $"  = Abilities \"{name}\"" : "";
					Console.WriteLine($"      {Describe(value),-22} x{n,-6}{named}");
				}
			}

			// Two histograms cannot say whether two fields vary together, and that is the whole question for the
			// 0xC9..0xCB word beside the direction ability - so cross-tabulate them.
			Console.WriteLine();
			Console.WriteLine("word 4 x word 5 (does the 0xC9..0xCB alphabet track the direction?):");
			foreach (var ((w4, w5), n) in joint.OrderByDescending(kv => kv.Value))
			{
				var named = abilities.TryGetValue(w5, out var an) ? an : w5 == 0 ? "-" : $"0x{w5:X}";
				Console.WriteLine($"      word4 {(w4 == 0 ? "-" : $"0x{w4:X2}"),-6}  word5 {named,-20} x{n}");
			}

			Console.WriteLine();
			Console.WriteLine("word 4 x the per-victim result's own type word (is word 4 just that, restated?):");
			foreach (var ((w4, rt), n) in versusResult.OrderByDescending(kv => kv.Value).Take(16))
				Console.WriteLine($"      word4 {(w4 == 0 ? "-" : $"0x{w4:X2}"),-6}  result type 0x{rt:X2}  x{n}");

			Console.WriteLine();
			Console.WriteLine("the DIRECTION ability x the per-victim result type (is the result type a direction too?):");
			foreach (var ((w5, rt), n) in versusDirection.OrderByDescending(kv => kv.Value).Take(24))
			{
				var named = abilities.TryGetValue(w5, out var dn) ? dn : w5 == 0 ? "-" : $"0x{w5:X}";
				Console.WriteLine($"      {named,-20} result type 0x{rt:X2}  x{n}");
			}

			Console.WriteLine();
			Console.WriteLine("by ACTOR - does a MOB's swing state a direction at all? (#673):");
			foreach (var ((player, field, value), n) in byActor.OrderBy(kv => kv.Key.Player).ThenBy(kv => kv.Key.Field).ThenByDescending(kv => kv.Value))
			{
				var named = abilities.TryGetValue(value, out var an) ? an : value == 0 ? "-" : $"0x{value:X}";
				Console.WriteLine($"      {(player ? "player" : "NPC   "),-7} {field,-16} {named,-20} x{n}");
			}

			Console.WriteLine();
			Console.WriteLine("PLAYER swings, split by whether the swing named a direction ABILITY (is an undirected swing directionless?):");
			foreach (var ((directed, rt), n) in playerUndirected.OrderByDescending(kv => kv.Key.Directed).ThenByDescending(kv => kv.Value))
				Console.WriteLine($"      {(directed ? "ability named" : "NO ability   "),-14} result type 0x{rt:X2}  x{n}");

			// The rose has three arcs and the wire has five zones, so the question that decides whether the swing pick
			// is a placeholder or a measurement is what the five collapse to. The collapse itself is the client's
			// (see `Directions`): a lower zone folds onto the upper one on the same side.
			var npcByCode = Directions.ToDictionary(d => d.Code, d => byActor.GetValueOrDefault((false, "result type", d.Code)));
			var npcTotal = npcByCode.Values.Sum();
			Console.WriteLine();
			Console.WriteLine($"the mob alphabet collapsed onto the THREE shield arcs ({npcTotal} NPC-actor swings):");
			foreach (var arc in new[] { "Left", "Mid", "Right" })
			{
				var members = Directions.Where(d => d.Arc == arc).ToArray();
				var n = members.Sum(d => npcByCode[d.Code]);
				var parts = string.Join(" + ", members.Select(d => $"{d.Name} x{npcByCode[d.Code]}"));
				Console.WriteLine($"      {arc,-6} {n,6}  {Share(n, npcTotal),6}   ({parts})");
			}

			// Chi-square against a uniform arc pick, 2 df: 5.99 is p=0.05, 9.21 is p=0.01. This is the number that
			// says whether the server's uniform draw is fitted to retail or merely unfalsified.
			var expected = npcTotal / 3.0;
			var chi = new[] { "Left", "Mid", "Right" }
				.Select(a => Directions.Where(d => d.Arc == a).Sum(d => npcByCode[d.Code]))
				.Sum(n => (n - expected) * (n - expected) / expected);
			Console.WriteLine($"      chi-square vs a uniform arc pick = {chi:F2} on 2 df (5.99 = p 0.05)");

			var upper = npcByCode[0xC9u] + npcByCode[0xCAu];
			var lower = npcByCode[0xCCu] + npcByCode[0xCDu];
			Console.WriteLine($"      within the two SIDE arcs, upper x{upper} / lower x{lower} = {Share(upper, upper + lower)} upper");

			// Is the five-vs-three split a property of the template? If some mobs only ever swing the three a
			// low-level player has, that is a per-archetype rule rather than a global distribution.
			var templatesWithEnough = byTemplate.Where(kv => kv.Value.Values.Sum() >= 8).OrderByDescending(kv => kv.Value.Values.Sum()).ToList();
			Console.WriteLine();
			Console.WriteLine($"per TEMPLATE - do some mobs swing only the three upper zones? ({byTemplate.Count} templates, {templatesWithEnough.Count} with >= 8 swings)");
			Console.WriteLine("template  name                       swings   UL   UR   OH   LL   LR  zones");
			foreach (var (template, counts) in templatesWithEnough.Take(30))
			{
				var n = counts.Values.Sum();
				var cells = string.Join(" ", Directions.Select(d => $"{counts.GetValueOrDefault(d.Code),4}"));
				var name = templateNames.GetValueOrDefault(template, "?");
				Console.WriteLine($"{template,8}  {(name.Length <= 24 ? name : name[..24]),-24} {n,6}  {cells}  {counts.Count(kv => kv.Value > 0)}");
			}

			var threeZone = templatesWithEnough.Count(kv => kv.Value.GetValueOrDefault(0xCCu) + kv.Value.GetValueOrDefault(0xCDu) == 0);
			Console.WriteLine($"      {threeZone} of {templatesWithEnough.Count} well-sampled templates NEVER swing a lower zone; "
			                  + $"{templatesWithEnough.Count - threeZone} do.");

			if (interesting.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"{interesting.Count} tail(s) with a NON-ZERO word past the selector - the ones that carry anything:");
			foreach (var (capture, frame, middle, targets, results) in interesting.Take(20))
				Console.WriteLine($"  {capture} frame {frame}  targets={targets} results={results}  [{string.Join(" ", middle.Select(Describe))}]");

			void Count(bool player, string field, uint value)
			{
				var key = (player, field, value);
				byActor[key] = byActor.GetValueOrDefault(key) + 1;
			}
		}

		private static string Label(int i) => i < MiddleSlots.Length ? i.ToString() : "2b";

		private static string Share(int part, int whole) => whole == 0 ? "-" : $"{100.0 * part / whole:0.0}%";

		// A word as the reader sees it plus, when it could plausibly be one, its float reading - the ticket's
		// standing hypothesis is that one of these is a charge fraction, and a float is unrecognisable in decimal.
		private static string Describe(uint value)
		{
			if (value == 0)
				return "0";

			var f = BitConverter.UInt32BitsToSingle(value);
			return float.IsFinite(f) && Math.Abs(f) is > 1e-6f and < 1e6f
				? $"0x{value:X8}({f:0.###}f)"
				: $"0x{value:X8}";
		}

		// The tail, read with the arithmetic BehaviorAcks documents: [u32 targetCount][targets x 8B][middle]
		// [u32 resultCount][results x 41B](+1 trailing byte for Combat:Ranged). Returns false when the lengths do not
		// close, which is the check that keeps this report honest rather than merely plausible.
		private static bool TryReadAttackTail(BehaviorControlCenterIIR bcc, out uint[] middle, out int targets, out int results, out List<uint> resultTypes,
			out List<uint> targetHandles)
		{
			middle = [];
			resultTypes = [];
			targetHandles = [];
			targets = results = 0;

			var tail = bcc.Tail;
			if (tail.Length < 4)
				return false;

			targets = (int)tail.ReadU32Be(0);
			var at = 4 + targets * 8;
			if (targets < 0 || at + 8 > tail.Length)
				return false;

			// Each target is a [class][handle] pair; only the handle is wanted, for the per-defender scope test.
			for (var i = 0; i < targets; i++)
				targetHandles.Add(tail.ReadU32Be(4 + i * 8 + 4));

			// The selector is the middle's second word and decides its own length - miss it and the result count lands
			// one word out, leaving the body unparseable.
			var count = tail.ReadU32Be(at + 4) > 1 ? MiddleSlots.Length + 1 : MiddleSlots.Length;
			if (at + count * 4 + 4 > tail.Length)
				return false;

			middle = new uint[count];
			for (var i = 0; i < count; i++)
				middle[i] = tail.ReadU32Be(at + i * 4);

			at += count * 4;
			results = (int)tail.ReadU32Be(at);
			var end = at + 4 + results * 41 + (bcc.BehaviorId == (uint)BehaviorId.CombatRanged ? 1 : 0);
			if (results < 0 || end != tail.Length)
				return false;

			// The per-victim result's own type word, at +13 of each 41-byte record - the alphabet word 4 is compared
			// against.
			for (var i = 0; i < results; i++)
				resultTypes.Add(tail.ReadU32Be(at + 4 + i * 41 + 13));

			return true;
		}
	}
}
