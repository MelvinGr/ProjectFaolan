using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	public static class PlayerSpawnScaffoldCodec
	{
		public const uint Marker = ListFrame.Unit;

		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR;

		public static byte[] Generate(PlayerSpawnScaffold s)
		{
			return new FaolanStream()
				.WriteArrayUInt32Length(payload =>
				{
					payload
						.WriteUInt32(GameServer0X2000Opcodes.SimpleCharFullUpdateIIR)
						.WriteUInt32(s.Ack)
						.WriteUInt32(s.Handle)
						.WriteUInt32(s.FieldA)
						.WriteByte(0x2C)
						.WriteByte(0x0A)
						.WriteProtobufByteLength((ProtoVec3)s.Position)
						.WriteByte(0x12)
						.WriteProtobufByteLength((ProtoQuat)s.Rotation);

					// Field 4 as a fixed32. Never observed: all 15787 captured FA015807/66AEDD50 transform groups are exactly
					// {0x0A vec3, 0x12 quat, 0x18 playfield}, and nothing in the codebase ever SETS Scale - it is only ever
					// parsed out of a body and written back. Kept as defensive pass-through, not as a modelled field.
					if (s.Scale != null)
						payload.WriteByte(0x25).WriteArray(s.Scale);

					payload.WriteByte(0x18).WriteVarint(s.Playfield)
						.WriteArray(PlayerSpawnPreFrameCodec.Build(s.Frame))
						.WriteProtobufByteLength(s.Character)
						.WriteArray(GeneratePostIir(s));
				})
				.ToArray();
		}

		public static bool TryParse(byte[] body, [NotNullWhen(true)] out PlayerSpawnScaffold? scaffold, [NotNullWhen(false)] out string? error)
		{
			scaffold = null;
			error = null;
			try
			{
				scaffold = Parse(body);
				return true;
			}
			catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
			{
				error = e.Message;
				scaffold = null;
				return false;
			}
		}

		private static byte[] GeneratePostIir(PlayerSpawnScaffold s)
		{
			var count = s.Entries.Count;
			using var b = new FaolanStream();

			// The header, the first record and the buff list - the part of this stream that is not the player's
			// (SpawnPostIirCommon). The player's first record IS the buff list's framing word, which is why its
			// length stays 4 whether the character has 0 or 3 buffs: the records are that list's ELEMENTS.
			s.Common.WritePrologue(b);

			b.WriteUInt32((uint)count).WriteUInt32(s.EntryTableX);
			if (count > 0)
				b.WriteUInt32(0x3F800000);
			foreach (var entry in s.Entries)
				b.WriteUInt32(entry.Index).WriteUInt32(entry.Flag).WriteUInt16(entry.Tag).WriteUInt32(entry.Value);

			if (count > 0)
			{
				foreach (var entry in s.IdList)
					b.WriteByte(entry.Rank).WriteUInt32(entry.Id);

				// ForCount(0) IS 0x3F1, so a spawn with an empty tail regenerates the exact byte it always did.
				b.WriteByte(s.IdListTerminatorRank).WriteUInt32(ListFrame.ForCount(s.IdListTail.Count));
				foreach (var tail in s.IdListTail)
					b.WriteUInt32(EntityClass.Character).WriteUInt32(tail.Id).WriteUInt32(0).WriteByte(tail.Kind);
			}
			else
				b.WriteUInt32(Marker);

			// The three closing pair lists, shared with the NPC stream byte for byte.
			s.Common.WriteTail(b);
			return b.ToArray();
		}

		private static PlayerSpawnScaffold Parse(byte[] body)
		{
			if (body == null || body.Length < 24)
				throw new FormatException("body too short");
			if (body.ReadU32Be(4) != (uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR)
				throw new FormatException("not an FA015807 body");
			if (body.ReadU32Be(0) != (uint)(body.Length - 4))
				throw new FormatException("body length field mismatch");

			var s = new PlayerSpawnScaffold
			{
				Ack = body.ReadU32Be(8),
				Handle = body.ReadU32Be(12),
				FieldA = body.ReadU32Be(16)
			};

			var i = 20;
			if (body[i++] != 0x2C)
				throw new FormatException("no 0x2C transform group");
			if (body[i] != 0x0A)
				throw new FormatException("no vec3");

			s.Position = body.AsSpan(i + 2, body[i + 1]).FromProtobuf<ProtoVec3>();
			i += 2 + body[i + 1];
			if (body[i] != 0x12)
				throw new FormatException("no quat");

			s.Rotation = body.AsSpan(i + 2, body[i + 1]).FromProtobuf<ProtoQuat>();
			i += 2 + body[i + 1];
			if (body[i] == 0x25)
			{
				s.Scale = body.AsSpan(i + 1, 4).ToArray();
				i += 5;
			}

			if (body[i++] != 0x18)
				throw new FormatException("no playfield varint");

			s.Playfield = ReadVarint(body, ref i);

			// Anchor the CharacterIIR by its name signature + length prefix (PlayerSpawnReader's proven scan).
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var iir))
				throw new FormatException("CharacterIIR not found");
			if (body.ReadU32Be(iir.Offset - 4) != (uint)iir.Length)
				throw new FormatException("CharacterIIR length prefix mismatch");

			if (!PlayerSpawnPreFrameCodec.TryParse(body.AsSpan(i, iir.Offset - 4 - i).ToArray(), out var frame))
				throw new FormatException($"the {iir.Offset - 4 - i}-byte pre-IIR frame does not walk as a spawn scaffold");

			s.Frame = frame;
			s.Character = iir.Message; // PlayerSpawnReader decoded it to locate it - there is nothing left to parse

			ParsePostIir(body, iir.Offset + iir.Length, s);
			return s;
		}

		private static void ParsePostIir(byte[] b, int i, PlayerSpawnScaffold s)
		{
			// The header, the first record and the buff list come from the shared reader (SpawnPostIirCommon), which
			// hands back where the ENTRY SLOT starts - the only part of this stream that is the player's own.
			s.Common = SpawnPostIirCommon.ReadPrologue(b, i, out i);

			// The header's two family words are ASSERTED rather than carried, because this codec only ever parses PLAYER
			// spawns and every captured one has 4 and 2. An NPC's 5 and 1/2/3 landing here means the body is not a player
			// scaffold, which is exactly what the caller wants to be told.
			if (s.Common.Header.FirstRecordLen != PlayerSpawnScaffold.PostIirFirstRecordLen)
				throw new FormatException($"post-IIR firstRecordLen {s.Common.Header.FirstRecordLen} is not the player's {PlayerSpawnScaffold.PostIirFirstRecordLen}");
			if (s.Common.Header.Word2 != PlayerSpawnScaffold.PostIirWord2)
				throw new FormatException($"post-IIR word2 {s.Common.Header.Word2} is not the player's {PlayerSpawnScaffold.PostIirWord2}");

			var count = U32(b, ref i);
			if (count > 0x1000)
				throw new FormatException($"implausible entry count {count}");

			s.EntryTableX = U32(b, ref i);
			if (count > 0 && U32(b, ref i) != 0x3F800000)
				throw new FormatException("entry-table weight (1.0) missing");

			for (var n = 0; n < count; n++)
			{
				var index = U32(b, ref i);
				var flag = U32(b, ref i);
				var tag = b.ReadU16Be(i);
				i += 2;
				s.Entries.Add(new ScaffoldEntry(index, tag, U32(b, ref i), flag));
			}

			// The ID list exists (0x3F1-group-terminated) iff the entry table is non-empty; otherwise a bare
			// u32 0x3F1 terminator stands in.
			if (count > 0)
			{
				while (true)
				{
					var rank = b[i++];
					var id = U32(b, ref i);

					// The closing group's "id" is a count-framed LIST WORD, not the constant 0x3F1 it was read as for
					// years - 0x3F1 is simply ForCount(0). Ids ascend, so a value that frames AND is not larger than
					// the last real id is the close, never a member. The end-exactness check below is what ultimately
					// proves the choice: guess wrong and the walk does not land on b.Length.
					if (ListFrame.TryCountOf(id, out var tailCount) && (s.IdList.Count == 0 || id < s.IdList[^1].Id))
					{
						// The terminator's rank byte is USUALLY zero and used to be asserted as such; 23 captured
						// spawns carry a non-zero one and still consume to exactly the body's end, so it is carried.
						s.IdListTerminatorRank = rank;
						for (var n = 0; n < tailCount; n++)
						{
							if (U32(b, ref i) != EntityClass.Character)
								throw new FormatException($"ID-list tail record {n} does not open with 0xC350");

							var refId = U32(b, ref i);
							if (U32(b, ref i) != 0)
								throw new FormatException($"ID-list tail record {n} zero field mismatch");

							s.IdListTail.Add(new ScaffoldTailRef(refId, b[i++]));
						}

						break;
					}

					// Ids ascend. Kept as a hard failure: it is what exposed the trap above, and a descending id that
					// does NOT frame means the walk diverged earlier and should say so rather than read on.
					if (s.IdList.Count > 0 && id < s.IdList[^1].Id)
						throw new FormatException("ID list not ascending");

					s.IdList.Add(new ScaffoldIdEntry(rank, id));
				}
			}
			else if (U32(b, ref i) != Marker)
				throw new FormatException("bare ID-list terminator missing");

			// The three closing pair lists, and the requirement that they land on the body's LAST byte, are shared with
			// the NPC stream (SpawnPostIirCommon) - the end-exactness is what proves the entry table above was read right.
			s.Common.ReadTail(b, i);
		}

		// Cursor reads over the FA015807 body. The parse path is offset-addressed (it anchors the CharacterIIR by
		// scan and references backward offsets), not a sequential FaolanStream read, so these stay byte-cursor based.
		private static ulong ReadVarint(byte[] b, ref int i)
		{
			var v = 0UL;
			var shift = 0;
			while (true)
			{
				var x = b[i++];
				v |= (ulong)(x & 0x7F) << shift;
				if ((x & 0x80) == 0)
					return v;

				shift += 7;
			}
		}

		private static uint U32(byte[] b, ref int i)
		{
			var v = b.ReadU32Be(i);
			i += 4;
			return v;
		}
	}
}
