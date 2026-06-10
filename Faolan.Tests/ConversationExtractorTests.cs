using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Importer;
using Xunit;

namespace Faolan.Tests
{
	// Locks the ported conversation extraction: a synthetic TalkToNpc (Interact 642CD3D6, actionType 0x16) followed
	// by server response packets must yield one node with the args (TypeId/Mode/Field3/NPC) and the responses, and
	// stop at the next talk. Plus the Merge dedup preference (4-byte-varint instance, then most packets).
	public class ConversationExtractorTests
	{
		[Fact]
		public void Extract_PairsTalkWithFollowingResponses()
		{
			var packets = new List<CapturedPacket>
			{
				Talk(42008, 2, 0, 0x9D42, 0x02000005),
				Response(0x2000, 0x4A3D899A),
				Response(0x2000, 0xF164A4A5),
				Response(0x2000, 0x12345678), // not a conversation sub-opcode -> ignored
				Response(0x2015, 0), // 0x2015 select -> included
				Talk(42009, 1, 0, 0x9D42, 0x02000005), // next talk -> window ends
				Response(0x2000, 0x4A3D899A) // belongs to the second talk
			};

			var entries = ConversationExtractor.Extract(packets);

			Assert.Equal(2, entries.Count);
			var first = entries[0];
			Assert.Equal(42008u, first.TypeId);
			Assert.Equal(2u, first.Mode);
			Assert.Equal(0u, first.Field3);
			Assert.Equal(0x9D42u, first.CapturedNpcHandle);
			Assert.Equal(0x02000005u, first.CapturedInstance);
			Assert.Equal(3, first.Packets.Count); // 4A3D899A + F164A4A5 + 0x2015 (the 0x12345678 is excluded)

			Assert.Equal(42009u, entries[1].TypeId);
			Assert.Single(entries[1].Packets);
		}

		[Fact]
		public void Extract_ResolvesNpcNameFromSpawn()
		{
			var packets = new List<CapturedPacket>
			{
				Spawn(0x9D42, "Casilda"), // FA015807 spawn for the handle the talk targets
				Talk(42008, 5, 0, 0x9D42, 0x02000005),
				Response(0x2000, 0x4A3D899A)
			};

			var entries = ConversationExtractor.Extract(packets);

			Assert.Single(entries);
			Assert.Equal("Casilda", entries[0].NpcName);
		}

		// The same NPC captured in two sessions has DIFFERENT handles but the same name; Merge keys by name, so they
		// collapse to one node (handle alone would have kept them as two, the bug behind wrong-NPC routing).
		[Fact]
		public void Merge_DedupesSameNpcAcrossDifferentHandles()
		{
			var sessionA = new ConversationEntry(42008, 5, 0, 0x4DEE9, 0x02000005, [[1]], "Casilda");
			var sessionB = new ConversationEntry(42008, 5, 0, 0x81906, 0x02000006, [
				[1],
				[2]
			], "Casilda"); // different handle, more packets

			var merged = ConversationExtractor.Merge([sessionA, sessionB]);

			Assert.Single(merged);
			Assert.Equal("Casilda", merged[0].NpcName);
			Assert.Equal(2, merged[0].Packets.Count); // the richer capture wins
		}

		[Fact]
		public void Merge_PrefersVarint4InstanceThenMostPackets()
		{
			var outOfRange = new ConversationEntry(1, 0, 0, 0x10, 0xFFFFFFFF, [
				[1],
				[2],
				[3]
			], "Casilda"); // 5-byte varint
			var inRange = new ConversationEntry(1, 0, 0, 0x10, 0x02000005, [[1]], "Casilda"); // 4-byte varint, fewer packets

			var merged = ConversationExtractor.Merge([outOfRange, inRange]);

			Assert.Single(merged);
			Assert.Equal(0x02000005u, merged[0].CapturedInstance); // in-range instance wins despite fewer packets
		}

		private static byte[] BuildInteractArgs(uint typeId, uint mode, uint field3, uint npc)
		{
			var b = new List<byte>();
			WriteField(b, 1, typeId);
			WriteField(b, 2, mode);
			WriteField(b, 3, field3);

			// field 10 = SpellTarget { field2 = npc handle }
			var sub = new List<byte>();
			WriteField(sub, 2, npc);
			b.Add((10 << 3) | 2);
			WriteVarint(b, (ulong)sub.Count);
			b.AddRange(sub);

			return b.ToArray();
		}

		private static byte[] Frame(byte[] body)
		{
			var full = new byte[12 + body.Length]; // [len4][crc4][headerLen4=0][body]
			body.CopyTo(full, 12);
			return full;
		}

		private static CapturedPacket Response(uint opcode, uint subOpcode)
		{
			return new CapturedPacket(0, true, opcode, subOpcode, Frame(new byte[8]));
		}

		// A download FA015807 spawn: 20-byte prefix [len][subop][c350][handle][fieldA] then the name as 0x0A<len><utf8>.
		private static CapturedPacket Spawn(uint handle, string name)
		{
			var nb = Encoding.ASCII.GetBytes(name);
			var body = new byte[20 + 2 + nb.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 0xFA015807);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), 0x0000C350);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), handle);
			body[20] = 0x0A;
			body[21] = (byte)nb.Length;
			nb.CopyTo(body, 22);
			return new CapturedPacket(0, true, 0x2000, 0xFA015807, Frame(body));
		}

		// Builds a full on-wire-ish packet [len4][crc4][headerLen4=0][body]; GetBody reads headerLen at [8..12]=0.
		private static CapturedPacket Talk(uint typeId, uint mode, uint field3, uint npc, uint instance)
		{
			var args = BuildInteractArgs(typeId, mode, field3, npc);

			// body: [bodylen4][642cd3d6][c350][inst][u16][actiontype=0x16][arglen4][args]
			var body = new byte[26 + args.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)body.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 0x642CD3D6);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), 0x0000C350);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), instance);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(18, 4), 0x16); // actionType TalkToNpc (overlaps the u16 slot, fine)
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(22, 4), (uint)args.Length);
			args.CopyTo(body, 26);

			return new CapturedPacket(0, false, 0x2000, 0x642CD3D6, Frame(body));
		}

		private static void WriteField(List<byte> b, int field, uint value)
		{
			b.Add((byte)((field << 3) | 0));
			WriteVarint(b, value);
		}

		private static void WriteVarint(List<byte> b, ulong v)
		{
			do
			{
				var x = (byte)(v & 0x7f);
				v >>= 7;
				if (v != 0)
					x |= 0x80;
				b.Add(x);
			}
			while (v != 0);
		}
	}
}
