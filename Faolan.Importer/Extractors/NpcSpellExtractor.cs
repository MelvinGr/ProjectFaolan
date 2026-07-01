using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Extractors
{
	// Harvests the spells caster NPCs cast at the player, for the NpcSpell table (#232). Retail NPC casts are
	// ApplySpell (0x2000 / 0x6B87E316) packets whose applyFrom is the casting NPC's entity handle and applyTo is the
	// player; this joins applyFrom -> the NPC's NAME via the same capture's FA015807 spawns (handles are
	// session-scoped, so spawns + casts MUST come from the same capture). The effect payload (bytes after applyFrom)
	// is kept verbatim - magnitude/duration aren't decoded yet.
	public sealed record NpcSpellEntry(string Name, uint SpellId, byte[] Payload);

	public static class NpcSpellExtractor
	{
		private const int ApplyFromOffset = 26;

		// Body offsets (inside the 0x2000 IIR carrier): [u32 len][u32 subop][u32 c350][u32 applyTo][u16 0]
		// [u32 spellId][u32 c350][u32 applyFrom][payload...].
		private const int ApplyToOffset = 12;
		private const int PayloadOffset = 30;
		private const int SpellIdOffset = 18;
		private const uint ApplySpellSubOpcode = 0x6B87E316;
		private const uint Opcode2000 = 0x2000;

		public static List<NpcSpellEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			// applyFrom (NPC entity handle) -> NPC name, from THIS capture's spawns (handles are session-scoped).
			var nameByHandle = new Dictionary<uint, string>();
			foreach (var spawn in NpcSpawnExtractor.Extract(packets))
			{
				if (!string.IsNullOrEmpty(spawn.Name))
					nameByHandle[spawn.Handle] = spawn.Name;
			}

			var entries = new List<NpcSpellEntry>();
			foreach (var p in packets)
			{
				if (p.Opcode != Opcode2000)
					continue;

				var body = GetBody(p.Bytes);
				if (body.Length < PayloadOffset)
					continue;
				if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != ApplySpellSubOpcode)
					continue;

				var applyTo = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(ApplyToOffset, 4));
				var spellId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(SpellIdOffset, 4));
				var applyFrom = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(ApplyFromOffset, 4));

				if (applyFrom == applyTo) // a self-buff, not an offensive cast
					continue;
				if (!nameByHandle.TryGetValue(applyFrom, out var name)) // not a known NPC caster (e.g. the player's own cast)
					continue;

				var len = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
				var end = Math.Min(body.Length, 4 + len);
				var payload = end > PayloadOffset
					? body[PayloadOffset..end]
					: [];
				if (payload.Length == 0) // the rank-1 EMPTY twin is the buff-end/terminator, not a standalone cast (#232)
					continue;

				entries.Add(new NpcSpellEntry(name, spellId, payload));
			}

			return entries;
		}

		// Dedupe across captures by (NPC name, spell id); keeps the first observed payload.
		public static List<NpcSpellEntry> Merge(IEnumerable<NpcSpellEntry> all)
		{
			var seen = new Dictionary<(string, uint), NpcSpellEntry>();
			foreach (var e in all)
				seen.TryAdd((e.Name, e.SpellId), e);
			return seen.Values.ToList();
		}

		private static byte[] GetBody(byte[] full)
		{
			if (full.Length < 12)
				return [];

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= full.Length
				? full[bodyStart..]
				: [];
		}
	}
}
