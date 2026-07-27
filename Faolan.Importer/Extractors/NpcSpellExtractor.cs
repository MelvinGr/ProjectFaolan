using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Combat;

using Faolan.Core.Network.Constants;

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
		// Body offsets (inside the 0x2000 IIR carrier): [u32 len][u32 subop][u32 c350][u32 applyTo][u16 0]
		// [u32 spellId][u32 c350][u32 applyFrom][payload...]. The header fields are read by NpcSpellCastBuilder;
		// PayloadOffset (30) is the fixed header length used as the minimum-body guard.
		private const int PayloadOffset = 30;
		private const uint ApplySpellSubOpcode = (uint)GameServer0X2000Opcodes.MagicBuffIIR;

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
				if (!p.IsIirCarrier)
					continue;

				var body = p.Body;
				if (body.Length < PayloadOffset)
					continue;
				if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != ApplySpellSubOpcode)
					continue;

				// The ApplySpell content is [subop][c350][applyTo][u16 0][spellId][c350][applyFrom][payload] - the same
				// shape NpcSpellCastBuilder builds, so parse it with that codec (applyTo@12 / spellId@18 / applyFrom@26
				// as before). Bound the stream to the declared IIR length so Payload matches the old len-bounded slice.
				var len = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
				var end = Math.Min(body.Length, 4 + len);
				if (end < PayloadOffset) // a length shorter than the fixed header: the old code yielded an empty payload -> skip
					continue;

				var cast = NpcSpellCastBuilder.NpcSpellCast.Read(new FaolanStream(body[4..end]));

				if (cast.ApplyFrom == cast.ApplyTo) // a self-buff, not an offensive cast
					continue;
				if (!nameByHandle.TryGetValue(cast.ApplyFrom, out var name)) // not a known NPC caster (e.g. the player's own cast)
					continue;
				if (cast.Payload.Length == 0) // the rank-1 EMPTY twin is the buff-end/terminator, not a standalone cast (#232)
					continue;

				entries.Add(new NpcSpellEntry(name, cast.SpellId, cast.Payload));
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

	}
}
