using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;

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
				if (p is not { IsIirCarrier: true, SubOpcode: MagicBuffCodec.Opcode })
					continue;

				// The ApplySpell content is [subop][c350][applyTo][env][spellBaseId][form][c350][applyFrom][data] -
				// MagicBuffCodec's record. A record too short to be one is a truncated capture, not a cast.
				MagicBuffIIR cast;
				try
				{
					cast = MagicBuffCodec.Parse(p.Record);
				}
				catch (CodecParseException)
				{
					continue;
				}

				if (cast.ApplyFrom == cast.ApplyTo) // a self-buff, not an offensive cast
					continue;
				if (!nameByHandle.TryGetValue(cast.ApplyFrom, out var name)) // not a known NPC caster (e.g. the player's own cast)
					continue;
				if (cast.Data.Length == 0) // the rank-1 EMPTY twin is the buff-end/terminator, not a standalone cast (#232)
					continue;

				entries.Add(new NpcSpellEntry(name, MagicBuffIds.WireIdOf(cast.SpellBaseId, cast.Form), cast.Data));
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
