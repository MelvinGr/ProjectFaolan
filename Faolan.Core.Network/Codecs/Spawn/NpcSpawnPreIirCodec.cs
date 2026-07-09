using System;
using System.Buffers.Binary;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Decodes / re-encodes the NPC pre-IIR scaffold (NpcSpawnPreIir) byte-exact. The scaffold sits between the
	// transform and the embedded CoreCharacterFullUpdateIIR. PlayerSpawnReader anchors the CoreCharacter (its
	// protobuf name is the first printable string in the body); the CoreChar version byte (0x41) is a fixed
	// 9-byte header before that name ([u8 0x41][u32][u32 msgLen]), and the count==0 scaffold is the fixed 223
	// bytes immediately before it. This typed codec is the foundation for sourcing the scaffold from DB (or
	// zeroing the diagnostic char buffer) instead of slicing it out of the captured Data blob (Gitea #185).
	public static class NpcSpawnPreIirCodec
	{
		// 9-byte embedded-CoreCharacter header before the protobuf name: [u8 version 0x41][u32][u32 msgLen].
		private const int CoreCharHeaderLength = 9;

		// Serializes the scaffold to its wire bytes (version + the interleaved u8/u32/sublist sequence).
		public static byte[] Encode(NpcSpawnPreIir scaffold)
		{
			var stream = new FaolanStream().WriteByte(scaffold.Version);
			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in NpcSpawnPreIir.Layout)
			{
				if (isSublist)
					stream.WriteArray(scaffold.Sublist);
				else if (isByte)
					stream.WriteByte(scaffold.Flags[bi++]);
				else
					stream.WriteUInt32(scaffold.Words[wi++]);
			}

			return stream.ToArray();
		}

		// Decodes the pre-IIR scaffold from an FA015807 body. `offset` returns the scaffold's start (the version
		// byte). Returns false if the body has no CharacterIIR, or the scaffold isn't the fixed count==0 form
		// (wrong version byte / the walk doesn't land exactly on the embedded CoreCharacter).
		public static bool TryDecode(byte[] body, out NpcSpawnPreIir scaffold, out int offset)
		{
			scaffold = null;
			offset = 0;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var coreCharOffset = read.Offset - CoreCharHeaderLength;
			var start = coreCharOffset - NpcSpawnPreIir.FixedLength;
			if (start < 0 || coreCharOffset > body.Length || body[start] != NpcSpawnPreIir.ExpectedVersion)
				return false;

			var result = new NpcSpawnPreIir
			{
				Version = body[start]
			};
			var o = start + 1;
			var bi = 0;
			var wi = 0;
			foreach (var (isByte, isSublist) in NpcSpawnPreIir.Layout)
			{
				if (isSublist)
				{
					if (o + 4 > coreCharOffset || BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(o, 4)) != 0x3F1u)
						return false; // count>0 sublist - variable-width, out of scope

					result.Sublist = body[o..(o + 4)];
					o += 4;
				}
				else if (isByte)
					result.Flags[bi++] = body[o++];
				else
				{
					result.Words[wi++] = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(o, 4));
					o += 4;
				}
			}

			if (o != coreCharOffset)
				return false; // didn't frame exactly up to the CoreCharacter - not the count==0 form

			scaffold = result;
			offset = start;
			return true;
		}
	}
}
