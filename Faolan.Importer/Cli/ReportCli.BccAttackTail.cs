using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report bcc-attack-tail`: the MIDDLE block of every captured attack tail, word by word (Gitea #661).
	//
	// The tail's shape has been settled since #536 - `[targets][middle][results]` accounts for every captured body -
	// but the middle block's MEANING was not. The client's reader is what says where each word goes
	// (`FUN_0084d5f0` then `FUN_01151170`, the shared Combat:Melee / :Unarmed reader; Ranged wraps it), so this
	// report prints each word under the Command-object slot the client stores it in, and its whole value
	// distribution beside it. A slot plus a distribution is what turns "eight opaque words" into a field.
	//
	// It reads the tail with the SAME arithmetic BehaviorAcks documents, so a change to that grammar breaks this
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
			// The rows where a middle word is NON-ZERO, so the report can show what such a hit looked like rather
			// than only that one exists.
			var interesting = new List<(string Capture, int Frame, uint[] Middle, int Targets, int Results)>();
			var joint = new Dictionary<(uint W4, uint W5), int>();
			var versusResult = new Dictionary<(uint W4, uint ResultType), int>();
			var versusDirection = new Dictionary<(uint W5, uint ResultType), int>();

			foreach (var c in captures)
			foreach (var p in c.Packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;
				if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || !attacks.Contains(bcc.BehaviorId))
					continue;

				if (!TryReadAttackTail(bcc, out var middle, out var targets, out var results, out var resultTypes))
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
				foreach (var rt in resultTypes)
				{
					var rkey = (middle[^4], rt);
					versusResult[rkey] = versusResult.GetValueOrDefault(rkey) + 1;
					var dkey = (middle[^3], rt);
					versusDirection[dkey] = versusDirection.GetValueOrDefault(dkey) + 1;
				}

				if (middle.Skip(2).Any(w => w != 0))
					interesting.Add((c.Name, p.Frame, middle, targets, results));
			}

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
			// resolve what the ids are: word 5 is an Abilities row (the swing DIRECTION), which is the finding this
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

			// Two histograms cannot say whether two fields VARY TOGETHER, and that is the whole question for the
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

			if (interesting.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"{interesting.Count} tail(s) with a NON-ZERO word past the selector - the ones that carry anything:");
			foreach (var (capture, frame, middle, targets, results) in interesting.Take(20))
				Console.WriteLine($"  {capture} frame {frame}  targets={targets} results={results}  [{string.Join(" ", middle.Select(Describe))}]");
		}

		private static string Label(int i) => i < MiddleSlots.Length ? i.ToString() : "2b";

		// A word as the reader sees it plus, when it could plausibly BE one, its float reading - the ticket's
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
		private static bool TryReadAttackTail(BehaviorControlCenterIIR bcc, out uint[] middle, out int targets, out int results, out List<uint> resultTypes)
		{
			middle = [];
			resultTypes = [];
			targets = results = 0;

			var tail = bcc.Tail;
			if (tail.Length < 4)
				return false;

			targets = (int)tail.ReadU32Be(0);
			var at = 4 + targets * 8;
			if (targets < 0 || at + 8 > tail.Length)
				return false;

			// The selector is the middle's SECOND word and decides its own length - miss it and the result count
			// lands one word out, which is what left 11 bodies unexplained before #536 found it.
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
