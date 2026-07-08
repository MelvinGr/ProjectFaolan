using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	public static class PlayerSpawnScaffoldCodec
	{
		public const uint Marker = 0x3F1;
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
				.WriteByte(0x2C)
				.WriteByte(0x0A)
				.WriteByte((byte)s.Vec3.Length)
				.WriteArray(s.Vec3)
				.WriteByte(0x12)
				.WriteByte((byte)s.Quat.Length)
				.WriteArray(s.Quat);
			if (s.Scale != null)
				payload.WriteByte(0x25).WriteArray(s.Scale);

			payload
				.WriteByte(0x18)
				.WriteVarint(s.Playfield)
				.WriteArray(s.PreFrame)
				.WriteUInt32((uint)s.CharacterIir.Length)
				.WriteArray(s.CharacterIir)
				.WriteArray(post);

			return new FaolanStream().WriteArrayUInt32Length(payload.ToArray()).ToArray();
		}

		public static bool TryParse(byte[] body, out PlayerSpawnScaffold scaffold, out string error)
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
			b.WriteUInt32(0).WriteUInt32(4).WriteUInt32(2).WriteUInt32(Marker * ((uint)s.BuffRecords.Count + 1));
			foreach (var rec in s.BuffRecords)
				b.WriteArray(rec);

			b.WriteUInt32((uint)count).WriteUInt32(s.EntryTableX);
			if (count > 0)
				b.WriteUInt32(0x3F800000);
			foreach (var entry in s.Entries)
				b.WriteUInt32(entry.Index).WriteUInt32(0).WriteUInt16(entry.Tag).WriteUInt32(entry.Value);

			if (count > 0)
			{
				foreach (var entry in s.IdList)
					b.WriteByte(entry.Rank).WriteUInt32(entry.Id);

				b.WriteByte(0).WriteUInt32(Marker);
			}
			else
				b.WriteUInt32(Marker);

			foreach (var pairs in new[]
			{
				s.EndPairs,
				s.EndPairs2
			})
			{
				b.WriteUInt32((uint)pairs.Count);
				foreach (var pair in pairs)
					b.WriteUInt32(pair.Id).WriteUInt32((uint)pair.Value);
			}

			b.WriteUInt32(Marker);
			return b.ToArray();
		}

		private static PlayerSpawnScaffold Parse(byte[] body)
		{
			if (body == null || body.Length < 24)
				throw new FormatException("body too short");
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != (uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR)
				throw new FormatException("not an FA015807 body");
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4)) != (uint)(body.Length - 4))
				throw new FormatException("body length field mismatch");

			var s = new PlayerSpawnScaffold
			{
				Ack = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8, 4)),
				Handle = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12, 4)),
				FieldA = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4))
			};

			var i = 20;
			if (body[i++] != 0x2C)
				throw new FormatException("no 0x2C transform group");
			if (body[i] != 0x0A)
				throw new FormatException("no vec3");

			s.Vec3 = body.AsSpan(i + 2, body[i + 1]).ToArray();
			i += 2 + body[i + 1];
			if (body[i] != 0x12)
				throw new FormatException("no quat");

			s.Quat = body.AsSpan(i + 2, body[i + 1]).ToArray();
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
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(iir.Offset - 4, 4)) != (uint)iir.Length)
				throw new FormatException("CharacterIIR length prefix mismatch");

			s.PreFrame = body.AsSpan(i, iir.Offset - 4 - i).ToArray();
			s.CharacterIir = body.AsSpan(iir.Offset, iir.Length).ToArray();

			ParsePostIir(body, iir.Offset + iir.Length, s);
			return s;
		}

		private static void ParsePostIir(byte[] b, int i, PlayerSpawnScaffold s)
		{
			if (U32(b, ref i) != 0 || U32(b, ref i) != 4)
				throw new FormatException("post-IIR header mismatch");
			if (U32(b, ref i) != 2)
				throw new FormatException("post-IIR entry intro mismatch");

			// The next field is 0x3F1 * (buffRecordCount + 1); each active buff/timer adds a 60-byte record.
			var key = U32(b, ref i);
			if (key == 0 || key % Marker != 0)
				throw new FormatException($"post-IIR key 0x{key:X} is not a multiple of 0x3F1");

			for (var n = key / Marker - 1; n > 0; n--)
			{
				if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 32, 4)) != EntityClass.Character)
					throw new FormatException("buff record shape mismatch");

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
				if (U32(b, ref i) != 0)
					throw new FormatException($"entry[{n}] zero field mismatch");

				var tag = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i, 2));
				i += 2;
				s.Entries.Add(new ScaffoldEntry(index, tag, U32(b, ref i)));
			}

			// The ID list exists (0x3F1-group-terminated) iff the entry table is non-empty; otherwise a bare
			// u32 0x3F1 terminator stands in.
			if (count > 0)
			{
				while (true)
				{
					var rank = b[i++];
					var id = U32(b, ref i);
					if (id == Marker)
					{
						if (rank != 0)
							throw new FormatException("ID-list terminator with non-zero rank");

						break;
					}

					if (s.IdList.Count > 0 && id < s.IdList[^1].Id)
						throw new FormatException("ID list not ascending");

					s.IdList.Add(new ScaffoldIdEntry(rank, id));
				}
			}
			else if (U32(b, ref i) != Marker)
				throw new FormatException("bare ID-list terminator missing");

			ReadPairs(b, ref i, s.EndPairs);
			ReadPairs(b, ref i, s.EndPairs2);
			if (U32(b, ref i) != Marker)
				throw new FormatException("final 0x3F1 marker missing");
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
			var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i, 4));
			i += 4;
			return v;
		}
	}
}
