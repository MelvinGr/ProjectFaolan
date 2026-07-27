using System;
using System.Collections.Generic;
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
		private const int BuffRecordSize = 60;

		public static byte[] Generate(PlayerSpawnScaffold s)
		{
			var post = GeneratePostIir(s);

			// Everything from SubOpcode onward; the leading u32 length prefix (= payload length) is added last.
			var payload = new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.SimpleCharFullUpdateIIR)
				.WriteUInt32(s.Ack)
				.WriteUInt32(s.Handle)
				.WriteUInt32(s.FieldA)
				// Position/Rotation go back out through the generated ProtoVec3/ProtoQuat - the same path
				// PlayerSpawnBuilder constructs with, and byte-identical to the payloads this codec parsed (proven by
				// the byte-exact regeneration of every captured spawn, not asserted here). #352 kept them raw on the
				// theory that a captured field-set would be lost; over 11,632 spawns the field set never varies.
				.WriteByte(0x2C)
				.WriteByte(0x0A)
				.WriteArrayByteLength(((ProtoVec3)s.Position).ToProtobuf())
				.WriteByte(0x12)
				.WriteArrayByteLength(((ProtoQuat)s.Rotation).ToProtobuf());
			// Field 4 as a fixed32. Never observed: all 15787 captured FA015807/66AEDD50 transform groups are exactly
			// {0x0A vec3, 0x12 quat, 0x18 playfield}, and nothing in the codebase ever SETS Scale - it is only ever
			// parsed out of a body and written back. Kept as defensive pass-through, not as a modelled field.
			if (s.Scale != null)
				payload.WriteByte(0x25).WriteArray(s.Scale);

			payload.WriteByte(0x18).WriteVarint(s.Playfield).WriteArray(s.PreFrame).WriteArrayUInt32Length(s.CharacterIir).WriteArray(post);

			return new FaolanStream().WriteArrayUInt32Length(payload.ToArray()).ToArray();
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
			var b = new FaolanStream();
			b.WriteUInt32((uint)s.HeadWords.Count);
			foreach (var word in s.HeadWords)
				b.WriteUInt32(word);

			b.WriteUInt32(4).WriteUInt32(2).WriteUInt32(Marker * ((uint)s.BuffRecords.Count + 1));
			foreach (var rec in s.BuffRecords)
				b.WriteArray(rec);

			b.WriteUInt32(count).WriteUInt32(s.EntryTableX);
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

			foreach (var pairs in new[] { s.EndPairs, s.EndPairs2 })
			{
				b.WriteUInt32(pairs.Count);
				foreach (var pair in pairs)
					b.WriteUInt32(pair.Id).WriteUInt32(pair.Value);
			}

			// ForCount(0) IS 0x3F1, so an empty third list regenerates the exact closing word it always did.
			b.WriteUInt32(ListFrame.ForCount(s.EndPairs3.Count));
			foreach (var pair in s.EndPairs3)
				b.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);

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

			s.Position = body.AsSpan(i + 2, body[i + 1]).ToArray().FromProtobuf<ProtoVec3>();
			i += 2 + body[i + 1];
			if (body[i] != 0x12)
				throw new FormatException("no quat");

			s.Rotation = body.AsSpan(i + 2, body[i + 1]).ToArray().FromProtobuf<ProtoQuat>();
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

			s.PreFrame = body.AsSpan(i, iir.Offset - 4 - i).ToArray();
			s.CharacterIir = body.AsSpan(iir.Offset, iir.Length).ToArray();

			ParsePostIir(body, iir.Offset + iir.Length, s);
			return s;
		}

		private static void ParsePostIir(byte[] b, int i, PlayerSpawnScaffold s)
		{
			// The post-IIR opens with a plain-counted list, then a constant 4. It was read as the constant pair [0][4]
			// because the list is empty on all but 2 captured spawns - those carry [1][60][4]. Same mistake as the two
			// count-FRAMED lists below, in the one spelling ListFrame does not cover: a bare count, not an n*0x3F1 word.
			var headCount = U32(b, ref i);
			if (headCount > 0x100)
				throw new FormatException($"implausible post-IIR head count {headCount}");

			for (var n = 0; n < headCount; n++)
				s.HeadWords.Add(U32(b, ref i));

			if (U32(b, ref i) != 4)
				throw new FormatException("post-IIR header mismatch");
			if (U32(b, ref i) != 2)
				throw new FormatException("post-IIR entry intro mismatch");

			// The next field is 0x3F1 * (buffRecordCount + 1); each active buff/timer adds a 60-byte record.
			var key = U32(b, ref i);
			if (key == 0 || key % Marker != 0)
				throw new FormatException($"post-IIR key 0x{key:X} is not a multiple of 0x3F1");

			// The record's word at +32 is 0xC350 on all but ONE captured record, which carries 0 - so it is a field of
			// the record, not a shape signature, and asserting it rejected a spawn whose walk was otherwise exact. The
			// count comes from the framing word above and the walk is proven by landing on b.Length, which is the check
			// that was doing the real work all along.
			for (var n = key / Marker - 1; n > 0; n--)
			{
				if (i + BuffRecordSize > b.Length)
					throw new FormatException($"buff record {n} runs past the body");

				s.BuffRecords.Add(b.AsSpan(i, BuffRecordSize).ToArray());
				i += BuffRecordSize;
			}

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

			ReadPairs(b, ref i, s.EndPairs);
			ReadPairs(b, ref i, s.EndPairs2);

			// The body's last field is a THIRD pair list, count-FRAMED rather than plain-counted. It was read as a
			// closing 0x3F1 constant because ForCount(0) is 0x3F1 and it is empty on all but 10 captured spawns - the
			// same trap as the ID-list close above.
			var endFrame = U32(b, ref i);
			if (!ListFrame.TryCountOf(endFrame, out var endCount))
				throw new FormatException($"final list word 0x{endFrame:X} is not count-framed");

			for (var n = 0; n < endCount; n++)
			{
				var id = U32(b, ref i);
				s.EndPairs3.Add(new ScaffoldPair(id, (int)U32(b, ref i)));
			}
			if (i != b.Length)
				throw new FormatException($"{b.Length - i} trailing bytes after the final marker");
		}

		private static void ReadPairs(byte[] b, ref int i, List<ScaffoldPair> pairs)
		{
			var n = U32(b, ref i);
			if (n > 0x100)
				throw new FormatException($"implausible pair count {n}");

			for (; n > 0; n--)
			{
				var id = U32(b, ref i);
				pairs.Add(new ScaffoldPair(id, (int)U32(b, ref i)));
			}
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
