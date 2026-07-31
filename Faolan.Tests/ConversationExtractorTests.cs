using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// Locks the answer-index-tree conversation extraction: a conversation INSTANCE = TalkToNpc(f3=0) [greeting] then
	// server responses + the player's KnubotAnswer(idx) picks. The greeting's responses are the "" screen; the
	// responses after picking idx are the "idx" screen; the next pick extends the path ("idx>idx2"). A new greeting
	// (TalkToNpc f3=0) starts a fresh instance. Merge unions screens across instances by (NpcName, TypeId, AnswerPath).
	public class ConversationExtractorTests
	{
		[Fact]
		public void Extract_BuildsAnswerIndexPathTree()
		{
			var packets = new List<CapturedPacket>
			{
				Talk(42008, 1, 0, 0x9D42, 0x02000005), // greeting
				Response(0x2000, 0x045B0B0F), // OpenWindow
				Response(0x2000, 0xF164A4A5), // AnswerList -> "" screen
				Answer(0), // pick option 0
				Response(0x2000, 0x4A3D899A), // AppendText -> "0" screen
				Response(0x2000, 0xF164A4A5),
				Answer(1), // pick option 1
				Response(0x2000, 0xC4AF39EB) // CloseWindow -> "0>1" screen
			};

			var entries = ConversationExtractor.Extract(packets);

			var byPath = entries.ToDictionary(e => e.AnswerPath);
			Assert.Equal(3, byPath.Count);
			Assert.Equal(2, byPath[""].Packets.Count);
			Assert.Equal(2, byPath["0"].Packets.Count);
			Assert.Single(byPath["0>1"].Packets);
			Assert.Equal(42008u, byPath[""].TypeId);
			Assert.Equal(0x9D42u, byPath[""].CapturedNpcHandle);
		}

		// A quest is GRANTED at the dialogue node where its full-update FIRST appears after the player picked an option
		// (path != ""). A full-update at the greeting (path "") or a re-send of an already-seen quest is NOT a grant
		// (those are login restores / re-displays). This is how "talk to NPC -> accept -> quest in journal" is harvested.
		[Fact]
		public void Extract_HarvestsQuestGrantAtAcceptNode()
		{
			var packets = new List<CapturedPacket>
			{
				Spawn(0x9D42, "Casilda"),
				Talk(42008, 1, 0, 0x9D42, 0x02000005), // greeting
				Response(0x2000, 0xF164A4A5), // "" screen
				QuestFullUpdate(13479), // at the greeting -> NOT a grant (and remembered)
				Answer(0),
				Response(0x2000, 0x4A3D899A), // "0" screen
				QuestFullUpdate(13479), // already seen -> NOT a grant
				Answer(0),
				Response(0x2000, 0x4A3D899A), // "0>0" screen
				QuestFullUpdate(13480) // first appearance, after a pick -> GRANT on "0>0"
			};

			var byPath = ConversationExtractor.Extract(packets).ToDictionary(e => e.AnswerPath);

			Assert.Equal(0u, byPath[""].GrantQuestId);
			Assert.Equal(0u, byPath["0"].GrantQuestId);
			Assert.Equal(13480u, byPath["0>0"].GrantQuestId);
		}

		[Fact]
		public void Extract_NewGreetingStartsFreshInstance()
		{
			var packets = new List<CapturedPacket>
			{
				Talk(42008, 1, 0, 0x9D42, 0x02000005), // walk 1 greeting
				Response(0x2000, 0xF164A4A5),
				Answer(0),
				Response(0x2000, 0x4A3D899A), // walk 1: "0"
				Talk(42008, 2, 0, 0x9D42, 0x02000005), // walk 2 greeting (new instance, path resets)
				Response(0x2000, 0xF164A4A5),
				Answer(1),
				Response(0x2000, 0x4A3D899A) // walk 2: "1"
			};

			var entries = ConversationExtractor.Extract(packets);
			var paths = entries.Select(e => e.AnswerPath).OrderBy(p => p).ToList();

			Assert.Equal(["", "", "0", "1"], paths); // "" twice (one per greeting), "0" from walk 1, "1" from walk 2
		}

		[Fact]
		public void Extract_NonGreetingTalkDoesNotResetPath()
		{
			var packets = new List<CapturedPacket>
			{
				Talk(42008, 1, 0, 0x9D42, 0x02000005), // greeting
				Response(0x2000, 0xF164A4A5),
				Talk(42008, 1, 2, 0x9D42, 0x02000005), // f3!=0 re-request (retail answers it with nothing) - NOT a boundary
				Answer(0),
				Response(0x2000, 0x4A3D899A)
			};

			var entries = ConversationExtractor.Extract(packets);

			// Two screens ("" and "0"), NOT three - the f3=2 talk neither started a new instance nor split a screen.
			Assert.Equal(2, entries.Count);
			Assert.Contains(entries, e => e.AnswerPath == "");
			Assert.Contains(entries, e => e.AnswerPath == "0");
		}

		[Fact]
		public void Extract_ResolvesNpcNameFromSpawn()
		{
			var packets = new List<CapturedPacket>
			{
				Spawn(0x9D42, "Casilda"),
				Talk(42008, 5, 0, 0x9D42, 0x02000005),
				Response(0x2000, 0x4A3D899A)
			};

			var entries = ConversationExtractor.Extract(packets);

			Assert.Single(entries);
			Assert.Equal("Casilda", entries[0].NpcName);
		}

		// Branches captured across re-walks UNION by answer-path: walk 1 contributes "0>0", walk 2 contributes
		// "0>1". Merge keeps both (distinct paths) and dedupes same-path screens by the RICHER capture - the one with
		// more response packets, which is the whole of the merge rule now that the varint term is gone.
		[Fact]
		public void Merge_UnionsBranchesAndDedupesByPath()
		{
			var greetingA = new ConversationEntry(42008, "", 0x4DEE9, [[1]], "Kalanthes");
			var greetingB = new ConversationEntry(42008, "", 0x81906, [[1], [2]], "Kalanthes"); // richer
			var branch0 = new ConversationEntry(42008, "0>0", 0x4DEE9, [[1]], "Kalanthes");
			var branch1 = new ConversationEntry(42008, "0>1", 0x81906, [[1]], "Kalanthes");

			var merged = ConversationExtractor.Merge([greetingA, greetingB, branch0, branch1]);

			var byPath = merged.ToDictionary(e => e.AnswerPath);
			Assert.Equal(3, byPath.Count); // "", "0>0", "0>1"
			Assert.Equal(2, byPath[""].Packets.Count); // the richer greeting capture wins
			Assert.True(byPath.ContainsKey("0>0") && byPath.ContainsKey("0>1"));
		}

		// A client KnubotAnswer (0x4D7D9123) whose body ends with the picked option index (u32 BE) - the branch key.
		private static CapturedPacket Answer(uint index)
		{
			var body = new byte[12];
			body.WriteU32Be(0, (uint)body.Length);
			body.WriteU32Be(4, 0x4D7D9123);
			body.WriteU32Be(8, index);
			return new CapturedPacket(0, false, 0x2000, 0x4D7D9123, Frame(body));
		}

		private static byte[] BuildInteractArgs(uint typeId, uint mode, uint field3, uint npc)
		{
			var b = new List<byte>();
			WriteField(b, 1, typeId);
			WriteField(b, 2, mode);
			WriteField(b, 3, field3);

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

		// A KnubotCloseChatWindow is the 0xC4AF39EB sub-op; a quest full-update is 0x63268258 with the quest id at
		// body offset 21 ([len4][subop4][ack4][player4][00000000][01][u32 questId]).
		private static CapturedPacket QuestFullUpdate(uint questId)
		{
			var body = new byte[25];
			body.WriteU32Be(0, (uint)body.Length);
			body.WriteU32Be(4, 0x63268258);
			body[20] = 0x01;
			body.WriteU32Be(21, questId);
			return new CapturedPacket(0, true, 0x2000, 0x63268258, Frame(body));
		}

		private static CapturedPacket Response(uint opcode, uint subOpcode)
		{
			return new CapturedPacket(0, true, opcode, subOpcode, Frame(new byte[8]));
		}

		// ⚠️ The name rides a REAL length-prefixed CoreCharacter message, serialized by the generated type (#541).
		// This used to lay down a bare `0x0A <len> <name>`, which is not a spawn - it only ever satisfied the lenient
		// byte scan the extractor has stopped using. `SpawnBodyReader.TryReadCharacterMessage` verifies the u32
		// length ahead of field 1 and re-decodes the message, so the fixture has to carry one.
		private static CapturedPacket Spawn(uint handle, string name)
		{
			var message = new ProtoCoreCharacterIIR
			{
				Name = name
			}.ToProtobuf();

			var body = new byte[20 + 4 + message.Length];
			body.WriteU32Be(0, (uint)body.Length);
			body.WriteU32Be(4, 0xFA015807);
			body.WriteU32Be(8, 0x0000C350);
			body.WriteU32Be(12, handle);
			body.WriteU32Be(20, (uint)message.Length); // the message's own length prefix
			message.CopyTo(body, 24);
			return new CapturedPacket(0, true, 0x2000, 0xFA015807, Frame(body));
		}

		private static CapturedPacket Talk(uint typeId, uint mode, uint field3, uint npc, uint instance)
		{
			var args = BuildInteractArgs(typeId, mode, field3, npc);

			var body = new byte[26 + args.Length];
			body.WriteU32Be(0, (uint)body.Length);
			body.WriteU32Be(4, 0x642CD3D6);
			body.WriteU32Be(8, 0x0000C350);
			body.WriteU32Be(12, instance);
			body.WriteU32Be(18, 0x16);
			body.WriteU32Be(22, (uint)args.Length);
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
