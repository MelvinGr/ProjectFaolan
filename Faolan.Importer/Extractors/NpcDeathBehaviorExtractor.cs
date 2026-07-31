using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Importer.Extractors
{
	// A harvested creature death animation: an NPC TYPE (by name) and the death-animation behaviorId retail sent in
	// its death BccIIR. Keyed by name (the stable NpcTemplate join key) because the wire handle is session-volatile.
	public sealed record NpcDeathBehavior(string Name, uint BehaviorId);

	// Harvests the per-creature death animation from captured NPC kills (#362). When an NPC dies, retail sends a
	// death-behavior BccIIR (0x642CD3D6) on its handle: seq 0x0004, behaviorId 0x0000001F, protobuf field-1 = the death
	// animation id. That id is CREATURE-TYPE specific (decoded across the corpus: 41877 = humanoid biped, 17036 =
	// demon/large caster, 215333 = quadruped/beast) - it selects which death animation plays; field-3 and field-12
	// vary per kill (hit-direction / ragdoll) and are NOT harvested. The dying handle is resolved to a creature NAME
	// via the FA015807 spawns streamed in the SAME capture (NpcSpawnExtractor's identity logic), so it maps onto the
	// NpcTemplate. Only creatures actually killed in a capture are recoverable; the rest default to the humanoid id.
	public static class NpcDeathBehaviorExtractor
	{
		private const uint DeathBehaviorId = 0x0000001F; // the behaviour the death record selects
		private const ushort DeathSeq = 0x0004; // the BCC phase word a slotted behaviour record carries

		public static List<NpcDeathBehavior> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			// handle -> creature name from this capture's NPC spawns (same identity logic the NpcSpawn import uses, so
			// the names line up with NpcTemplate.Name). Player spawns are excluded by NpcSpawnExtractor already.
			var handleName = new Dictionary<uint, string>();
			foreach (var e in NpcSpawnExtractor.Extract(packets))
			{
				if (!string.IsNullOrEmpty(e.Name))
					handleName[e.Handle] = e.Name;
			}

			var results = new List<NpcDeathBehavior>();
			foreach (var p in packets)
			{
				if (p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: BehaviorControlCenterCodec.Opcode })
					continue;

				BehaviorControlCenterIIR bcc;
				try
				{
					bcc = BehaviorControlCenterCodec.Parse(p.Record);
				}
				catch (CodecParseException)
				{
					continue; // a truncated capture, not a death record
				}

				if (bcc.Seq != DeathSeq || bcc.BehaviorId != DeathBehaviorId)
					continue;

				var behaviorId = DeathAnimationId(bcc.Args);
				if (behaviorId == 0)
					continue;

				// Attribute only to a KNOWN npc handle (a player death carries the same behaviour record but its handle
				// is in the instance range, so it never appears in handleName).
				if (handleName.TryGetValue(bcc.Actor, out var name))
					results.Add(new NpcDeathBehavior(name, behaviorId));
			}

			return results;
		}

		// Merge harvests across captures to one behaviorId per NAME. A creature's death id is consistent in the corpus;
		// if a name were ever seen with two ids, the MAJORITY wins (defensive - none observed).
		public static List<NpcDeathBehavior> Merge(IEnumerable<NpcDeathBehavior> all)
		{
			return all.GroupBy(d => d.Name).Select(g => new NpcDeathBehavior(g.Key, g.GroupBy(d => d.BehaviorId).OrderByDescending(x => x.Count()).First().Key)).ToList();
		}

		// The death animation id out of the behaviour's `CommandMessage` args: field 1 (tag 0x08), the first thing the
		// death record's protobuf carries. Read off the ARGS rather than the whole body, because that is the region
		// BehaviorControlCenterCodec hands over; 0 when the args are not a death record's.
		//
		// Not `FromProtobuf<CommandMessage>`: the harvest wants the raw varint, and a re-encode of fields we do not
		// model is exactly what the codec's comment warns against.
		private static uint DeathAnimationId(byte[] args)
		{
			if (args.Length < 2 || args[0] != 0x08)
				return 0;

			var r = new SpanReader(args, nameof(NpcDeathBehaviorExtractor));
			r.Skip(1);
			return (uint)r.ReadVarint();
		}
	}
}
