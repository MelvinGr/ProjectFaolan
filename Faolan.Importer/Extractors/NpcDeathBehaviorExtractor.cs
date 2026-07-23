using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;

namespace Faolan.Importer.Extractors
{
	// A harvested creature death animation: an NPC TYPE (by name) and the death-animation behaviorId retail sent in
	// its death BccIIR. Keyed by name (the stable NpcTemplate join key) because the wire handle is session-volatile.
	public sealed record NpcDeathBehavior(string Name, uint BehaviorId);

	// Harvests the per-creature death animation from captured NPC kills (#362). When an NPC dies, retail sends a
	// death-behavior BccIIR (0x642CD3D6) on its handle: form 0x0004, slot 0x0000001F, protobuf field-1 = the death
	// behaviorId. That id is CREATURE-TYPE specific (decoded across the corpus: 41877 = humanoid biped, 17036 =
	// demon/large caster, 215333 = quadruped/beast) - it selects which death animation plays; field-3 and field-12
	// vary per kill (hit-direction / ragdoll) and are NOT harvested. The dying handle is resolved to a creature NAME
	// via the FA015807 spawns streamed in the SAME capture (NpcSpawnExtractor's identity logic), so it maps onto the
	// NpcTemplate. Only creatures actually killed in a capture are recoverable; the rest default to the humanoid id.
	public static class NpcDeathBehaviorExtractor
	{
		private const uint DeathBccSubOpcode = (uint)GameServer0X2000Opcodes.BehaviorControlCenterIIR; // 0x642CD3D6
		private const uint DeathSlot = 0x0000001F; // the slot the death behavior lands in
		private const uint Opcode2000 = 0x2000;
		private const ushort DeathForm = 0x0004; // BccIIR form for a slotted behavior record

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
				if (p.Opcode != Opcode2000 || !p.IsDownload || p.SubOpcode != DeathBccSubOpcode)
					continue;

				var body = WorldObjectExtractor.GetBody(p.Bytes);
				if (!TryParseDeathBehavior(body, out var handle, out var behaviorId))
					continue;

				// Attribute only to a KNOWN npc handle (a player death carries the same slot-0x1F record but its handle
				// is in the instance range, so it never appears in handleName).
				if (handleName.TryGetValue(handle, out var name))
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

		private static ulong ReadVarint(byte[] b, ref int i)
		{
			ulong value = 0;
			var shift = 0;
			while (i < b.Length)
			{
				var x = b[i++];
				value |= (ulong)(x & 0x7F) << shift;
				if ((x & 0x80) == 0)
					break;

				shift += 7;
			}

			return value;
		}

		// The death BccIIR body: [0:4] len [4:8] subop [8:12] class [12:16] handle [16:18] form [18:22] slot
		// [22:26] pbLen [26..] protobuf. A death record is form 0x0004, slot 0x1F, protobuf starting with field-1
		// (tag 0x08) whose varint is the death behaviorId.
		private static bool TryParseDeathBehavior(byte[] body, out uint handle, out uint behaviorId)
		{
			handle = 0;
			behaviorId = 0;
			if (body.Length < 27)
				return false;
			if (BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(16, 2)) != DeathForm)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(18, 4)) != DeathSlot)
				return false;
			if (body[26] != 0x08) // protobuf field 1 (the behaviorId)
				return false;

			handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4));
			var offset = 27;
			behaviorId = (uint)ReadVarint(body, ref offset);
			return behaviorId != 0;
		}
	}
}
