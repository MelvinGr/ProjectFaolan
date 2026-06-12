using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Faolan.GameServer.Spawn
{
	// The structured form of a player SimpleCharFullUpdate (FA015807) body: everything around the embedded
	// ProtoCoreCharacterIIR, per the validated grammar in docs/archive/player-spawn-generation-plan.md ("VALIDATED
	// GRAMMAR"). Parse + Generate round-trip byte-exact over every captured player-own spawn (proven by
	// Captures/parse_spawn_scaffold.py over all 34, locked here by PlayerSpawnScaffoldCodecTests on the
	// representative golden fixtures). This is what lets the spawn scaffold live as DB rows instead of the
	// captured template/burst bytes.
	public sealed class PlayerSpawnScaffold
	{
		// The embedded ProtoCoreCharacterIIR bytes (typed round-trip via FromProtobuf/ToProtobufBytes is
		// byte-identical; see PlayerSpawnGenerationTests).
		public byte[] CharacterIir { get; set; }

		// The pre-IIR frame between the transform and the CharacterIIR length prefix: a near-constant skeleton
		// with per-character value islands (228 bytes editor-constant / 239 normal / 339 geared+table). Kept raw;
		// PlayerSpawnPreFrame builds the fresh-character variant.
		public byte[] PreFrame { get; set; }

		public byte[] Quat { get; set; } // TODO: COnvert to Quaterion, or 4 floats
		public byte[] Scale { get; set; } // null when absent

		// Transform component payloads (the 0x2C group), kept as raw field payloads: 0x0A vec3 (3 x fixed32),
		// 0x12 quat (4 x fixed32), optional 0x25 scale (4 bytes), 0x18 playfield varint.
		public byte[] Vec3 { get; set; } // todo: coNVERT TO VECTOR3 OR 3 FLAOTS

		// Post-IIR stream. Buff/timer records are 60-byte transient state (only on captured chars with active
		// buffs); the entry table / ID list / end pairs are the per-character skill+feat scaffold.
		public List<byte[]> BuffRecords { get; } = new List<byte[]>();
		public List<ScaffoldEntry> Entries { get; } = new List<ScaffoldEntry>();
		public List<ScaffoldIdEntry> IdList { get; } = new List<ScaffoldIdEntry>();
		public List<ScaffoldPair> EndPairs { get; } = new List<ScaffoldPair>();
		public List<ScaffoldPair> EndPairs2 { get; } = new List<ScaffoldPair>();
		public uint Ack { get; set; } = 0xC350;

		public uint EntryTableX { get; set; } // per-char u32 after the entry count (Strie 0xB7, necrol80 0x2B6, editor 0x3F1)
		public uint FieldA { get; set; }
		public uint Handle { get; set; }
		public ulong Playfield { get; set; }
	}

	// One uniform 14-byte entry [u32 index][u32 0][u16 tag][u32 value]: the weighted attributes read as
	// tag=value/value=0x3F800000 (weight 1.0), the feats as tag=(type<<8|subindex)/value=float bits.
	public readonly record struct ScaffoldEntry(uint Index, ushort Tag, uint Value);

	// One 5-byte ID-list group [u8 rank][u32 id]; ids ascend, the id 0x3F1 terminates the list on the wire.
	public readonly record struct ScaffoldIdEntry(byte Rank, uint Id);

	// One 8-byte ending pair [u32 id][s32 value] (ids are decades 10..110).
	public readonly record struct ScaffoldPair(uint Id, int Value);

	public static class PlayerSpawnScaffoldCodec
	{
		public const uint Marker = 0x3F1;
		public const uint SubOpcode = 0xFA015807;
		private const int BuffRecordSize = 60;

		public static byte[] Generate(PlayerSpawnScaffold s)
		{
			var post = GeneratePostIir(s);
			var transformLen = 1 + 2 + s.Vec3.Length + 2 + s.Quat.Length + (s.Scale != null
				? 5
				: 0) + 1 + VarintLength(s.Playfield);
			var total = 20 + transformLen + s.PreFrame.Length + 4 + s.CharacterIir.Length + post.Length;

			var body = new byte[total];
			var i = 0;
			WriteU32(body, ref i, (uint)(total - 4));
			WriteU32(body, ref i, SubOpcode);
			WriteU32(body, ref i, s.Ack);
			WriteU32(body, ref i, s.Handle);
			WriteU32(body, ref i, s.FieldA);
			body[i++] = 0x2C;
			body[i++] = 0x0A;
			body[i++] = (byte)s.Vec3.Length;
			s.Vec3.CopyTo(body, i);
			i += s.Vec3.Length;
			body[i++] = 0x12;
			body[i++] = (byte)s.Quat.Length;
			s.Quat.CopyTo(body, i);
			i += s.Quat.Length;
			if (s.Scale != null)
			{
				body[i++] = 0x25;
				s.Scale.CopyTo(body, i);
				i += 4;
			}

			body[i++] = 0x18;
			WriteVarint(body, ref i, s.Playfield);
			s.PreFrame.CopyTo(body, i);
			i += s.PreFrame.Length;
			WriteU32(body, ref i, (uint)s.CharacterIir.Length);
			s.CharacterIir.CopyTo(body, i);
			i += s.CharacterIir.Length;
			post.CopyTo(body, i);
			return body;
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
			var size = 8 + 8 + s.BuffRecords.Count * BuffRecordSize + 8 + (count > 0
				? 4
				: 0) + count * 14 + (count > 0
				? (s.IdList.Count + 1) * 5
				: 4) + 4 + s.EndPairs.Count * 8 + 4 + s.EndPairs2.Count * 8 + 4;
			var b = new byte[size];
			var i = 0;
			WriteU32(b, ref i, 0);
			WriteU32(b, ref i, 4);
			WriteU32(b, ref i, 2);
			WriteU32(b, ref i, Marker * ((uint)s.BuffRecords.Count + 1));
			foreach (var rec in s.BuffRecords)
			{
				rec.CopyTo(b, i);
				i += BuffRecordSize;
			}

			WriteU32(b, ref i, (uint)count);
			WriteU32(b, ref i, s.EntryTableX);
			if (count > 0)
				WriteU32(b, ref i, 0x3F800000);
			foreach (var entry in s.Entries)
			{
				WriteU32(b, ref i, entry.Index);
				WriteU32(b, ref i, 0);
				BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i, 2), entry.Tag);
				i += 2;
				WriteU32(b, ref i, entry.Value);
			}

			if (count > 0)
			{
				foreach (var entry in s.IdList)
				{
					b[i++] = entry.Rank;
					WriteU32(b, ref i, entry.Id);
				}

				b[i++] = 0;
				WriteU32(b, ref i, Marker);
			}
			else
				WriteU32(b, ref i, Marker);

			foreach (var pairs in new[] { s.EndPairs, s.EndPairs2 })
			{
				WriteU32(b, ref i, (uint)pairs.Count);
				foreach (var pair in pairs)
				{
					WriteU32(b, ref i, pair.Id);
					WriteU32(b, ref i, (uint)pair.Value);
				}
			}

			WriteU32(b, ref i, Marker);
			return b;
		}

		private static PlayerSpawnScaffold Parse(byte[] body)
		{
			if (body == null || body.Length < 24)
				throw new FormatException("body too short");
			if (BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)) != SubOpcode)
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

			s.Vec3 = body
				.AsSpan(i + 2, body[i + 1])
				.ToArray();
			i += 2 + body[i + 1];
			if (body[i] != 0x12)
				throw new FormatException("no quat");

			s.Quat = body
				.AsSpan(i + 2, body[i + 1])
				.ToArray();
			i += 2 + body[i + 1];
			if (body[i] == 0x25)
			{
				s.Scale = body
					.AsSpan(i + 1, 4)
					.ToArray();
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

			s.PreFrame = body
				.AsSpan(i, iir.Offset - 4 - i)
				.ToArray();
			s.CharacterIir = body
				.AsSpan(iir.Offset, iir.Length)
				.ToArray();

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
				if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 32, 4)) != 0xC350)
					throw new FormatException("buff record shape mismatch");

				s.BuffRecords.Add(b
					.AsSpan(i, BuffRecordSize)
					.ToArray());
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

		private static int VarintLength(ulong v)
		{
			var n = 1;
			while ((v >>= 7) != 0)
				n++;
			return n;
		}

		private static void WriteU32(byte[] b, ref int i, uint v)
		{
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i, 4), v);
			i += 4;
		}

		private static void WriteVarint(byte[] b, ref int i, ulong v)
		{
			while (v >= 0x80)
			{
				b[i++] = (byte)(v | 0x80);
				v >>= 7;
			}

			b[i++] = (byte)v;
		}
	}
}
