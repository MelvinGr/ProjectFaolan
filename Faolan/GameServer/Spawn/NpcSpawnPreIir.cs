using System;
using System.Buffers.Binary;
using System.Text;
using Faolan.Core.Data;

namespace Faolan.GameServer.Spawn
{
	// Typed, mutable view of the NPC FA015807 (SimpleCharFullUpdate) PRE-IIR scaffold - the "last un-lifted part"
	// of the from-DB NPC spawn (Gitea #185). The scaffold is the fixed read sequence
	// SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00) runs after the transform and before the embedded
	// CoreCharacterFullUpdateIIR: a u8 version (0x05), then 6 u8 + 53 u32 interleaved with one count-framed
	// sublist (docs/fa015807-spawn-reader.md, recovered from Ghidra and validated over 4558 captured records).
	//
	// The 53 u32 are read in WIRE order but scattered into NON-CONTIGUOUS C++ struct fields 0x184..0x264; the
	// inline char buffer over that region holds the NPC's RDB script-binding diagnostic string (ScriptGroup /
	// Script / Command names + <font color=...> markup), null-padded. So the block carries NO session-volatile
	// instance handles - the only stale reference in an NPC spawn is the playfield token in the transform
	// (SpawnPlayfieldRewriter), which is why a carried-over scaffold re-addresses cleanly once the token is fixed.
	//
	// This is the count==0 form (the per-spawn sublist is empty on every observed beach/login NPC); a count>0
	// sublist is variable-width and out of scope here (NpcSpawnPreIirCodec.TryDecode returns false on it).
	public sealed class NpcSpawnPreIir
	{
		public const byte ExpectedVersion = 0x05;
		public const int WordCount = 53;
		public const int ByteCount = 6;

		// Total scaffold size on the wire for the count==0 form: u8 version + 6 u8 + 53 u32 + the 4-byte sublist
		// framing word (0x3F1). 1 + 6 + 53*4 + 4 = 223.
		public const int FixedLength = 1 + ByteCount + WordCount * 4 + 4;

		// The wire read order (after the version byte): true == u8 field, false == u32 field. The single sublist
		// is held out of this table (it sits between the 32nd and 33rd entry) and serialized as raw bytes.
		private static readonly bool[] IsByteField =
		[
			true,                                                   // wi0
			false, false, false, false, false, false, false, false, // wi1..8
			false, false, false, false, false, false, false, false, // wi9..16
			false,                                                  // wi17
			true, true, true,                                       // wi18..20
			false, false, false, false,                             // wi21..24
			true, true,                                             // wi25..26
			false, false, false, false, false                       // wi27..31
			// SUBLIST here (wi32)
			// + 27 u32 (wi33..59) appended below in BuildLayout
		];

		// The C++ struct offset each of the 53 u32 is stored to, in WIRE order. Only used to reassemble the
		// script-binding char buffer (DecodeScriptText); irrelevant to byte-exact wire round-trip.
		internal static readonly int[] WordStructOffsets =
		[
			0x184, 0x188, 0x18c, 0x190, 0x194, 0x198, 0x19c, 0x1a8, 0x1b0, 0x1c0, 0x1c8, 0x1d0, 0x1d8, 0x1e0, 0x1e8, 0x1f0, 0x1b8,
			0x1fc, 0x200, 0x204, 0x208,
			0x210, 0x214, 0x218, 0x21c, 0x220,
			0x1a0, 0x1a4, 0x1ac, 0x1b4, 0x1c4, 0x1cc, 0x1d4, 0x1dc, 0x1e4, 0x1ec, 0x1f4, 0x1bc,
			0x224, 0x230, 0x228, 0x234, 0x238, 0x23c, 0x240, 0x244, 0x248, 0x24c, 0x254, 0x258, 0x25c, 0x260, 0x264,
		];

		// The full wire layout: the 32 pre-sublist fields (IsByteField), the sublist, then 27 trailing u32.
		// Entry == (isByte, isSublist).
		internal static readonly (bool IsByte, bool IsSublist)[] Layout = BuildLayout();

		// version byte (0x05; client rejects any other value).
		public byte Version { get; set; } = ExpectedVersion;

		// The 6 single-byte fields, in wire order (read positions 0, 18, 19, 20, 25, 26).
		public byte[] Flags { get; set; } = new byte[ByteCount];

		// The 53 u32 fields, in wire order, each held as the big-endian value read off the wire (the value the
		// client byteswaps into its struct). Re-emitted big-endian, so the round-trip is byte-exact regardless of
		// what the words mean. DecodeScriptText re-interprets them as the little-endian char buffer.
		public uint[] Words { get; set; } = new uint[WordCount];

		// The per-spawn sublist, raw. count==0 (the only observed form) is the bare 0x3F1 framing word.
		public byte[] Sublist { get; set; } = [0x00, 0x00, 0x03, 0xF1];

		// Reassembles the 53 u32 in C++ struct-offset order, little-endian (the stream byteswaps on read, so the
		// wire is LE), into the contiguous inline char buffer, and returns its printable runs joined by ' | '.
		// This is the NPC's script-binding diagnostic string - a per-instance text view, for diagnostics/tests.
		public string DecodeScriptText()
		{
			const int baseOffset = 0x184;
			var span = WordStructOffsets[^1] - baseOffset + 4; // 0x264 - 0x184 + 4 = 228
			var buffer = new byte[span];
			for (var i = 0; i < WordCount; i++)
			{
				// Word is stored big-endian; the struct char buffer holds the bytes little-endian (reversed).
				var at = WordStructOffsets[i] - baseOffset;
				buffer[at + 0] = (byte)(Words[i] & 0xFF);
				buffer[at + 1] = (byte)((Words[i] >> 8) & 0xFF);
				buffer[at + 2] = (byte)((Words[i] >> 16) & 0xFF);
				buffer[at + 3] = (byte)((Words[i] >> 24) & 0xFF);
			}

			var sb = new StringBuilder();
			var run = new StringBuilder();
			foreach (var c in buffer)
			{
				if (c is >= 0x20 and < 0x7F)
				{
					run.Append((char)c);
				}
				else
				{
					if (run.Length >= 6)
						sb.Append(sb.Length > 0 ? " | " : "").Append(run);
					run.Clear();
				}
			}

			if (run.Length >= 6)
				sb.Append(sb.Length > 0 ? " | " : "").Append(run);
			return sb.ToString();
		}

		private static (bool, bool)[] BuildLayout()
		{
			var layout = new (bool, bool)[ByteCount + WordCount + 1]; // 6 u8 + 53 u32 + 1 sublist = 60 entries
			var k = 0;
			foreach (var isByte in IsByteField)
				layout[k++] = (isByte, false);
			layout[k++] = (false, true); // the sublist (wi32)
			for (var i = 0; i < 27; i++) // the 27 trailing u32 (wi33..59)
				layout[k++] = (false, false);
			return layout;
		}
	}

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

			var result = new NpcSpawnPreIir { Version = body[start] };
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
				{
					result.Flags[bi++] = body[o++];
				}
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
	}
}
