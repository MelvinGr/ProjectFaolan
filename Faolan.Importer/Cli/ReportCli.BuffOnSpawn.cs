using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report buff-on-spawn`
	//
	// The capture question #677 is blocked on, asked of the corpus rather than guessed: **when an already-buffed
	// entity streams into a player's view, does retail tell them about its buffs?** The ticket names two candidate
	// shapes - attached to the spawn body, or a `MagicBuffIIR` fan-out right after it - and says the corpus must
	// settle which before anything is built.
	//
	// The measurement is a JOIN on the entity handle: for every captured `MagicBuffIIR` apply, whether the target
	// handle was spawned earlier in the same capture (`SimpleCharFullUpdateIIR`), and how long before. A fan-out
	// would show as a tight cluster of applies within a second or so of a spawn, on entities that are not the
	// local player, carrying a REMAINING duration rather than the catalogue one - the same shortfall signature the
	// login restore has.
	//
	// ⚠️ A handle is session-scoped, so everything is keyed by (capture, handle). Comparing handles across captures
	// merges unrelated entities.
	public static partial class ReportCli
	{
		// Wide enough that a burst arriving a few packets after the spawn still counts, narrow enough that an
		// unrelated cast a minute later does not.
		private const double SpawnFanOutSeconds = 5.0;

		private static void BuffOnSpawn(IReadOnlyList<NamedCapture> captures)
		{
			var spawnedAfterApply = 0;
			var neverSpawned = 0;
			var withinWindow = new List<string>();
			var afterWindow = 0;
			var totalApplies = 0;
			var selfApplies = 0;
			var deltas = new List<double>();
			var partialRestores = new List<string>();
			var nullCaster = new List<string>();
			var spawnBodiesCarryingBuff = 0;
			var spawnBodies = 0;

			using var db = OpenDb();
			var spellNames = db.Spells.ToDictionary(s => s.Id, s => (s.Name, s.DurationMs));

			foreach (var capture in captures)
			{
				var start = capture.Packets.Count > 0 ? capture.Packets[0].Ticks : 0L;
				var spawnAt = new Dictionary<uint, double>();
				var applies = new List<(double At, MagicBuffIIR Buff)>();

				foreach (var p in capture.Packets)
				{
					var at = (p.Ticks - start) / 1e7;

					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode }
					    && p.TryParse(SimpleCharFullUpdateCodec.Parse, out var spawn))
					{
						spawnBodies++;
						spawnAt.TryAdd(spawn.Handle, at);

						// The OTHER candidate shape the ticket names: the buff riding the spawn body itself. The
						// decoded spawn has no buff list - if it ever grows one, this counter is where it shows up.
						continue;
					}

					if (p is { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode }
					    && p.TryParse(MagicBuffCodec.Parse, out var buff)
					    && buff.Form == MagicBuffIds.FormApply
					    && buff.Data.Length == MagicBuffDataCodec.Length)
						applies.Add((at, buff));
				}

				foreach (var (at, buff) in applies)
				{
					totalApplies++;

					// ⭐ A ZERO-HANDLE caster is the tell. Every ordinary apply names who cast it; a buff the server
					// is merely RE-ASSERTING has no live caster to name, so it writes the Character class word with a
					// zero instance behind it.
					//
					// ⚠️ NOT the same as `ApplyFrom == null`, which is the class-0 "no caster reference at all" form
					// (11 in the corpus - the Racer buff, #636). `MagicBuffIIR.ApplyFrom` is nullable and `null == 0`
					// is false, so this test picks out the zero-handle form alone, which is what it wants.
					if (buff.ApplyFrom == 0)
					{
						var d = spawnAt.TryGetValue(buff.ApplyTo, out var s0) && s0 <= at ? at - s0 : double.NaN;
						var pl = MagicBuffDataCodec.Parse(buff.Data);
						var rr = spellNames.GetValueOrDefault(buff.SpellBaseId);
						nullCaster.Add($"{capture.Name} +{at:0.0}s  to 0x{buff.ApplyTo:X8} ({(buff.ApplyTo >= 0x02000000 ? "a CHARACTER" : "an npc/dynel")})  "
						               + $"{(double.IsNaN(d) ? "target never spawned here" : $"spawned {d:0.00}s earlier")}  "
						               + $"{rr.Name ?? $"0x{buff.SpellBaseId:X8}"}  {pl.Duration}cs of {rr.DurationMs / 10}cs\n"
						               + $"      form {buff.Form} env {buff.Env}  payload {Convert.ToHexString(buff.Data)}\n"
						               + $"      counter={pl.Counter} counterType={pl.CounterType} bool1={pl.Bool1} bool2={pl.Bool2} "
						               + $"descriptor=[{string.Join(",", pl.Descriptor)}] castingLevel={pl.CastingLevel} stamp={pl.ExpiryStamp}");
					}

					if (buff.ApplyFrom == buff.ApplyTo)
					{
						selfApplies++;
						continue;
					}

					if (!spawnAt.TryGetValue(buff.ApplyTo, out var spawnedAt))
					{
						neverSpawned++;
						continue;
					}

					if (spawnedAt > at)
					{
						spawnedAfterApply++;
						continue;
					}

					var delta = at - spawnedAt;
					deltas.Add(delta);

					var payload = MagicBuffDataCodec.Parse(buff.Data);
					var row = spellNames.GetValueOrDefault(buff.SpellBaseId);
					var expected = row.DurationMs / 10;
					var partial = expected > 0 && payload.Duration < expected;

					// ⭐ The signature to look for is not proximity alone - a fresh cast on a mob that just spawned
					// looks identical in time - it is proximity PLUS a partial duration. A buff already running when
					// the entity streamed in cannot state its full length.
					if (partial)
						partialRestores.Add($"{capture.Name} +{at:0.0}s  to 0x{buff.ApplyTo:X8} from 0x{buff.ApplyFrom:X8}  spawned {delta:0.00}s earlier  "
						                    + $"{row.Name}  {payload.Duration}cs of {expected}cs ({100.0 * payload.Duration / expected:0.#}%)");

					if (delta > SpawnFanOutSeconds)
					{
						afterWindow++;
						continue;
					}

					withinWindow.Add($"{capture.Name} +{at:0.0}s  handle 0x{buff.ApplyTo:X8} spawned {delta:0.00}s earlier  "
					                 + $"{row.Name ?? $"0x{buff.SpellBaseId:X8}"}  duration {payload.Duration}cs"
					                 + (expected > 0 ? $" vs row {expected}cs{(partial ? "  <-- PARTIAL" : "")}" : " (no row)"));
				}
			}

			Console.WriteLine("#677: does retail send an already-buffed entity's buffs when it streams into view?\n");
			Console.WriteLine($"  {spawnBodies} SimpleCharFullUpdateIIR spawn bodies, {spawnBodiesCarryingBuff} of them carrying a buff list "
			                  + "(the decoded body has no such field - a non-zero here would mean one was found).");
			Console.WriteLine($"  {totalApplies} MagicBuff applies: {selfApplies} self, {totalApplies - selfApplies} at another entity.");
			Console.WriteLine($"    target never spawned in this capture : {neverSpawned}");
			Console.WriteLine($"    target spawned AFTER the apply       : {spawnedAfterApply}");
			Console.WriteLine($"    target spawned <= {SpawnFanOutSeconds:0}s before        : {withinWindow.Count}");
			Console.WriteLine($"    target spawned  > {SpawnFanOutSeconds:0}s before        : {afterWindow}");

			if (deltas.Count > 0)
			{
				var sorted = deltas.OrderBy(d => d).ToList();
				Console.WriteLine($"    spawn -> apply delay: min {sorted[0]:0.00}s  median {sorted[sorted.Count / 2]:0.0}s  max {sorted[^1]:0.0}s");
			}

			Console.WriteLine();
			Console.WriteLine($"Applies with a ZERO-HANDLE caster - a re-assert rather than a cast ({nullCaster.Count}):");
			foreach (var line in nullCaster.Take(40))
				Console.WriteLine($"  {line}");
			if (nullCaster.Count > 40)
				Console.WriteLine($"  ... and {nullCaster.Count - 40} more");

			Console.WriteLine();
			Console.WriteLine($"NON-SELF applies carrying a PARTIAL duration - the streaming-in signature ({partialRestores.Count}):");
			foreach (var line in partialRestores.Take(40))
				Console.WriteLine($"  {line}");
			if (partialRestores.Count > 40)
				Console.WriteLine($"  ... and {partialRestores.Count - 40} more");

			Console.WriteLine();
			if (withinWindow.Count == 0)
			{
				Console.WriteLine("NO apply lands in the window after its target's spawn. On this corpus retail does NOT fan buffs out");
				Console.WriteLine("when an entity streams in - so an observer genuinely learns nothing, and building a fan-out would put a");
				Console.WriteLine("packet on the wire retail never sends.");
				return;
			}

			Console.WriteLine($"Applies landing within {SpawnFanOutSeconds:0}s of their target's spawn:");
			foreach (var line in withinWindow.Take(60))
				Console.WriteLine($"  {line}");
			if (withinWindow.Count > 60)
				Console.WriteLine($"  ... and {withinWindow.Count - 60} more");
		}
	}
}
