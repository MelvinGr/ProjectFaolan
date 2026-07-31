using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The count-framed SUBLIST that sits between the 32nd and 33rd field of the FA015807 pre-IIR scaffold - the one
	// variable-width part of a read sequence that is otherwise a fixed grid of 6 u8 + 53 u32 (#466 / #474).
	//
	// PLAYER and NPC spawns run the same client reader (FUN_01121c00), so they carry the same list. It read as three
	// magic constants on the player side and as a raw 4-byte `0x3F1` on the NPC side, for the same reason in both
	// cases: it is EMPTY on every NPC and holds exactly one unnamed entry on a normal login, so the framing word
	// looked like a terminator. On a player it is the character's saved FEAT SPECS - named, with a trained-feat list
	// each - which is what put a name to the whole structure.
	//
	// A SUB-RECORD codec (#530 §2 rule 5c): composed inside SpawnPreIirCodec, never sent alone, so it declares no
	// opcode and exposes only the internal composition pair.
	internal static class SpawnPreFrameSublistCodec
	{
		internal static void Read(ref SpanReader r, List<SpawnPreFrameSublistEntry> into)
		{
			var count = r.ReadListCount();
			for (var e = 0; e < count; e++)
			{
				var entry = new SpawnPreFrameSublistEntry
				{
					Key = r.ReadU32Be(),
					Flag = r.ReadU8(),
					Name = Encoding.Latin1.GetString(r.ReadBytesU16Length())
				};

				var ids = r.ReadListCount();
				for (var k = 0; k < ids; k++)
					entry.FeatIds.Add(r.ReadU32Be());

				into.Add(entry);
			}
		}

		internal static void Write(FaolanStream stream, IReadOnlyList<SpawnPreFrameSublistEntry> entries)
		{
			stream.WriteUInt32(ListFrame.ForCount(entries.Count));
			foreach (var entry in entries)
			{
				stream.WriteUInt32(entry.Key).WriteByte(entry.Flag).WriteString(entry.Name).WriteUInt32(ListFrame.ForCount(entry.FeatIds.Count));
				foreach (var id in entry.FeatIds)
					stream.WriteUInt32(id);
			}
		}

		// The list's own width on the wire: its framing word plus each entry's
		// [u32 key][u8 flag][u16 len + name][count word][4 x id].
		internal static int ByteCount(IReadOnlyList<SpawnPreFrameSublistEntry> entries)
		{
			var bytes = 4;
			foreach (var entry in entries)
				bytes += 4 + 1 + 2 + entry.Name.Length + 4 + 4 * entry.FeatIds.Count;

			return bytes;
		}
	}

	// One entry of that sublist. On a PLAYER it is a saved FEAT SPEC:
	//
	//   [u32 key][u8 flag][u16 len + Latin1 name][count-framed list of u32 feat ids]
	//
	// Every normal login carries exactly one entry with key 0, flag 0 and an EMPTY name - which is why the frame's
	// bytes 119..121 read as "3 reserved zero bytes" and the whole entry read as a lead-in to a bare feat list.
	// Captured frames with two and four entries are NAMED: "PvE - Lance" (23 feats), "PvP" (27), "PvP -
	// Decap/Protection", "PvP - Decap/Armor", "Hybrid" - the character's saved feat specs, each with its own
	// trained-feat list. Key and flag ascend with the entry index on those frames (0/0, 1/1, ...). The ids are in
	// TRAIN order, not sorted.
	//
	// On an NPC the list is empty in 11,394 of 11,400 captured spawns; the six that carry one carry the same
	// key-0/flag-0/unnamed/no-ids entry a fresh player does, so nothing suggests a second meaning for the structure.
	public sealed class SpawnPreFrameSublistEntry
	{
		public List<uint> FeatIds { get; } = [];
		public byte Flag { get; set; }
		public uint Key { get; set; }
		public string Name { get; set; } = "";
	}
}
