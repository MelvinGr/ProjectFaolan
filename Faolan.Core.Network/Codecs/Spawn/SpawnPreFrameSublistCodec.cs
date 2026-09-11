using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The count-framed sublist that sits between the 32nd and 33rd field of the spawn pre-IIR scaffold - the one
	///     variable-width part of a read sequence that is otherwise a fixed grid of 6 u8 and 53 u32.
	/// </summary>
	/// <remarks>
	///     Player and NPC spawns run the same client reader (FUN_01121c00), so they carry the same list. It is empty on
	///     every NPC and holds exactly one unnamed entry on a normal login, which makes its framing word read as a
	///     terminator; on a player it is the character's saved feat specs, named, each with its own trained-feat list.
	///     <para>
	///         A sub-record codec: composed inside <see cref="SpawnPreIirCodec" /> and never sent alone, so it declares no
	///         opcode and exposes only the internal composition pair.
	///     </para>
	/// </remarks>
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

		/// <summary>
		///     The list's own width on the wire: its framing word plus each entry's
		///     [u32 key][u8 flag][u16 len + name][count word][4 x id].
		/// </summary>
		internal static int ByteCount(IReadOnlyList<SpawnPreFrameSublistEntry> entries)
		{
			var bytes = 4;
			foreach (var entry in entries)
				bytes += 4 + 1 + 2 + entry.Name.Length + 4 + 4 * entry.FeatIds.Count;

			return bytes;
		}
	}

	/// <summary>
	///     One entry of that sublist. On a player it is a saved feat spec:
	///     <c>[u32 key][u8 flag][u16 len + Latin1 name][count-framed list of u32 feat ids]</c>.
	/// </summary>
	/// <remarks>
	///     Every normal login carries exactly one entry with key 0, flag 0 and an empty name, which is what makes the entry
	///     look like a lead-in to a bare feat list. Captured frames with two and four entries are named - "PvE - Lance" with
	///     23 feats, "PvP" with 27, "PvP - Decap/Protection", "PvP - Decap/Armor", "Hybrid" - and are the character's saved
	///     feat specs, each with its own trained-feat list. Key and flag ascend with the entry index on those frames, and
	///     the ids are in train order rather than sorted.
	///     <para>
	///         On an NPC the list is empty in 11,394 of 11,400 captured spawns, and the six that carry one carry the same
	///         key-0, flag-0, unnamed, no-ids entry a fresh player does, so nothing suggests a second meaning for the
	///         structure.
	///     </para>
	/// </remarks>
	public sealed class SpawnPreFrameSublistEntry
	{
		public List<uint> FeatIds { get; } = [];
		public byte Flag { get; set; }
		public uint Key { get; set; }
		public string Name { get; set; } = "";
	}
}
