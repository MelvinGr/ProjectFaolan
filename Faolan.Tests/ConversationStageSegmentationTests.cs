using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// A captured conversation node bundles every stage the player walked (greeting + each option's reply). The
	// runtime segments it so a click serves the greeting and each KnubotAnswer serves the next reply, instead of
	// dumping the whole tree at once. A stage ends at an answer-list (0xF164A4A5) or close-window (0xC4AF39EB).
	public class ConversationStageSegmentationTests
	{
		private const uint OpenWindow = 0x045B0B0F;
		private const uint AppendText = 0x4A3D899A;
		private const uint AnswerList = 0xF164A4A5;
		private const uint CloseWindow = 0xC4AF39EB;

		[Fact]
		public void Segments_AtAnswerListAndCloseWindow_OneStagePerExchange()
		{
			// A bundled three-stage conversation: greeting+options, an option reply+options, then a closing reply.
			var packets = new List<byte[]>
			{
				Packet(OpenWindow), Packet(AppendText), Packet(AnswerList), // stage 0
				Packet(AppendText), Packet(AnswerList), // stage 1
				Packet(AppendText), Packet(CloseWindow) // stage 2 (ends the conversation)
			};

			var stages = GameServerListener.SegmentConversationStages(packets);

			Assert.Equal(3, stages.Count);
			Assert.Equal(3, stages[0].Count); // OpenWindow + AppendText + AnswerList
			Assert.Equal(2, stages[1].Count); // AppendText + AnswerList
			Assert.Equal(2, stages[2].Count); // AppendText + CloseWindow
		}

		[Fact]
		public void Segments_TrailingPacketsWithoutTerminator_FormAFinalStage()
		{
			var packets = new List<byte[]>
			{
				Packet(OpenWindow), Packet(AnswerList), // stage 0
				Packet(AppendText) // a trailing reply with no answer-list/close -> its own final stage
			};

			var stages = GameServerListener.SegmentConversationStages(packets);

			Assert.Equal(2, stages.Count);
			Assert.Single(stages[1]);
		}

		[Fact]
		public void Segments_SingleStage_WhenNoFurtherOptions()
		{
			var packets = new List<byte[]> { Packet(OpenWindow), Packet(AppendText), Packet(AnswerList) };

			var stages = GameServerListener.SegmentConversationStages(packets);

			Assert.Single(stages);
			Assert.Equal(3, stages[0].Count);
		}

		// A minimal on-wire packet: [len][crc][headerLen=0][u32 bodyLen][u32 subOpcode]. SegmentConversationStages
		// reads the sub-opcode at body[4:8] (body starts at 12 + headerLen).
		private static byte[] Packet(uint subOpcode)
		{
			var packet = new byte[20];
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), 0); // headerLen = 0 -> body at offset 12
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), 12); // body length field
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), subOpcode);
			return packet;
		}
	}
}
