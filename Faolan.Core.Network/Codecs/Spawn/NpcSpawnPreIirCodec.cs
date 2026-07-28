using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

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
					SpawnPreFrameSublist.Write(stream, scaffold.Sublist);
				else if (isByte)
					stream.WriteByte(scaffold.Flags[bi++]);
				else
					stream.WriteUInt32(scaffold.Words[wi++]);
			}

			return stream.ToArray();
		}

		// Decodes the pre-IIR scaffold from an FA015807 body. `offset` returns the scaffold's start (the version
		// byte). Returns false if the body has no CharacterIIR, or the walk doesn't land exactly on the embedded
		// CoreCharacter.
		//
		// ⚠️ The start is WALKED to, not computed. It used to be `coreCharOffset - FixedLength`, which assumes the
		// scaffold's one variable-width field - the sublist - is empty. On the six captured spawns whose list has an
		// entry that subtraction lands in the middle of a preceding field, so they were rejected outright. Walking
		// the body's own header is the route PlayerSpawnScaffoldCodec already takes, and it does not care how wide
		// the scaffold turns out to be.
		public static bool TryDecode(byte[] body, [NotNullWhen(true)] out NpcSpawnPreIir? scaffold, out int offset)
		{
			scaffold = null;
			offset = 0;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;
			if (!TryWalkToScaffold(body, out var start))
				return false;

			var coreCharOffset = read.Offset - CoreCharHeaderLength;
			if (start >= coreCharOffset || coreCharOffset > body.Length || body[start] != NpcSpawnPreIir.ExpectedVersion)
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
					if (!SpawnPreFrameSublist.TryRead(body, ref o, result.Sublist))
						return false;
				}
				else if (isByte)
					result.Flags[bi++] = body[o++];
				else
				{
					result.Words[wi++] = body.ReadU32Be(o);
					o += 4;
				}
			}

			if (o != coreCharOffset)
				return false; // didn't frame exactly up to the CoreCharacter - so this walk was not the scaffold

			scaffold = result;
			offset = start;
			return true;
		}

		// [u32 len][u32 subop][u32 ack][u32 handle][u32 fieldA][0x2C transform group][0x18 playfield varint], and
		// then the scaffold. The same header PlayerSpawnScaffoldCodec walks; only the offset is wanted here.
		private static bool TryWalkToScaffold(byte[] body, out int start)
		{
			start = 0;
			if (body.Length < 24 || body.ReadU32Be(4) != (uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR)
				return false;

			var i = 20;
			if (body[i++] != 0x2C || body[i] != 0x0A)
				return false;

			i += 2 + body[i + 1];
			if (i + 1 >= body.Length || body[i] != 0x12)
				return false;

			i += 2 + body[i + 1];
			if (i >= body.Length)
				return false;
			if (body[i] == 0x25)
				i += 5;
			if (i >= body.Length || body[i++] != 0x18)
				return false;

			while (i < body.Length && (body[i++] & 0x80) != 0)
			{
			}

			start = i;
			return i < body.Length;
		}
	}
}
