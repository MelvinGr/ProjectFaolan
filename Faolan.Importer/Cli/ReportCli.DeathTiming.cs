using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report death-timing`: every captured death `BehaviorControlCenterIIR`, grouped by its animation, with each
	// record's actor resolved to a creature name.
	//
	// `field12` (`ActivationTime`) reads as the death animation's own length in centiseconds, and three of the four
	// animations hold perfectly still across every record of them - but 17036, the demon and large-caster one, takes
	// eight values. The candidate explanation is that one animation is shared by many creatures and the length is the
	// creature's, 17036 being the only one of the four whose population spans several creature types. So the report
	// resolves every actor and asks whether `field12` is constant per creature.
	//
	// The actor is resolved the way `NpcDeathBehaviorExtractor` does it - against the FA015807 spawns of the same
	// capture, because a handle is only unique inside one session. A record whose actor never spawned in the capture
	// (a player death, or a creature streamed before the dump starts) is reported separately rather than dropped
	// silently: an unresolved population is the one thing that could fake either answer.
	public static partial class ReportCli
	{
		// The death record: BehaviorId 0x1F at phase 0x0004, whose args' field 1 is the animation.
		private const uint DeathBehaviorId = 0x0000001F;
		private const ushort DeathSeq = 0x0004;

		private static void DeathTiming(IReadOnlyList<NamedCapture> captures)
		{
			var rows = new List<(string Capture, uint Animation, uint Field12, string Actor)>();

			foreach (var c in captures)
			{
				var names = new Dictionary<uint, string>();
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
				{
					if (!string.IsNullOrEmpty(e.Name))
						names[e.Handle] = e.Name!;
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
						continue;

					if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || bcc.Seq != DeathSeq || bcc.BehaviorId != DeathBehaviorId)
						continue;

					var args = bcc.Args.FromProtobuf<CommandMessage>();
					if (args.BccActionId == 0)
						continue;

					rows.Add((c.Name, args.BccActionId, args.ActivationTime, names.GetValueOrDefault(bcc.Actor, $"(unresolved 0x{bcc.Actor:X8})")));
				}
			}

			if (rows.Count == 0)
			{
				Console.WriteLine("no death records in the corpus.");
				return;
			}

			var resolved = rows.Count(r => !r.Actor.StartsWith('('));
			Console.WriteLine($"=== death records: {rows.Count}, actor resolved on {resolved} ({100.0 * resolved / rows.Count:0.0}%) ===");
			Console.WriteLine();

			foreach (var anim in rows.GroupBy(r => r.Animation).OrderByDescending(g => g.Count()))
			{
				var values = anim.Select(r => r.Field12).Distinct().OrderBy(v => v).ToList();
				var modal = anim.GroupBy(r => r.Field12).OrderByDescending(g => g.Count()).First();
				Console.WriteLine($"animation {anim.Key,-8} {anim.Count(),4} record(s), field12 takes {values.Count} value(s) - modal {modal.Key} x{modal.Count()}");

				if (values.Count == 1)
					continue;

				// The question: does the creature explain the spread? A creature carrying one value on every record of
				// it is consistent with "the length is the creature's"; one carrying several falsifies it outright,
				// which is a stronger result than any amount of agreement.
				var byActor = anim.Where(r => !r.Actor.StartsWith('(')).GroupBy(r => r.Actor).ToList();
				var single = byActor.Count(g => g.Select(r => r.Field12).Distinct().Count() == 1);
				Console.WriteLine($"    resolved to {byActor.Count} creature(s); {single} carry ONE value, {byActor.Count - single} carry several");

				foreach (var actor in byActor.OrderBy(g => g.Key))
				{
					var per = actor.GroupBy(r => r.Field12).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}");
					Console.WriteLine($"      {actor.Key,-36} {string.Join(" ", per)}");
				}

				var unresolvedValues = anim.Where(r => r.Actor.StartsWith('(')).GroupBy(r => r.Field12).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}").ToList();
				if (unresolvedValues.Count > 0)
					Console.WriteLine($"      {"(unresolved actors)",-36} {string.Join(" ", unresolvedValues)}");
			}
		}
	}
}
