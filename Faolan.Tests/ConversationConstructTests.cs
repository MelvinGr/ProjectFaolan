using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// #188: ConversationService.ServeScreen now CONSTRUCTS the spoken line (KnubotAppendText) and window-open
	// (KnubotOpenChatWindow) through KnubotCodec instead of replaying the captured packet with byte-substitution.
	// These tests prove the swap is behavior-preserving: the constructed record is BYTE-IDENTICAL to what the old
	// handle-substitution produced, and TryConstructKnubotRecord lifts/rebuilds the record out of a full packet.
	public class ConversationConstructTests
	{
		private const uint CapturedInstance = 0x110099BD;
		private const uint CapturedNpc = 0x00042194;
		private const uint ClickedNpc = 0x00090000;
		private const uint LiveInstance = 0x02000123;

		// SetDialogueCamera (#188): decode a real captured camera packet into columns and rebuild it byte-for-byte
		// (with the captured handles) - so it can be constructed from the columns instead of replayed.
		[Fact]
		public void BuildDialogueCameraPacket_ReproducesTheCapturedPacket()
		{
			// Real SetDialogueCamera packet (GameCharAgent:0x58 -> GameCharInterface:0x02165eff, opcode 0x2015).
			var captured = Convert.FromHexString("000000397100ae7d000000170a070d5db9eca91058120a0d91f7878b10ffbdd9102015" + "0000c3500004de8a0000c35002165eff05010119000000000000");

			var cols = ConversationScreenDecoder.Decode(captured);
			Assert.NotNull(cols);
			Assert.Equal(ConversationScreenDecoder.SetDialogueCamera, cols.SubOpcode);
			Assert.Equal(0x58u, cols.CameraAgentInstance);

			// The captured body's two handles: [0xC350][npc][0xC350][player] at 12 + headerLen.
			var body = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(8, 4));
			var npc = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(body + 4, 4));
			var player = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(body + 12, 4));

			var rebuilt = DialogueCameraBody.BuildDialogueCameraPacket(cols.CameraAgentInstance!.Value, player, npc, cols.CameraParams);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(rebuilt));
		}

		// The structured-column construct (#188) must be BYTE-IDENTICAL to the Data-path construct, so persisting the
		// decoded screen and building from it doesn't change the wire output.
		[Fact]
		public void BuildFromColumns_IsByteIdenticalToTheDataPath()
		{
			foreach (var recordHex in new[] { KnubotCodecTestData.AppendTextBody, KnubotCodecTestData.AnswerListBody, KnubotCodecTestData.CloseChatWindowBody })
			{
				var packet = WrapInPacket(Convert.FromHexString(recordHex));

				var cols = ConversationScreenDecoder.Decode(packet);
				Assert.NotNull(cols);
				var p = new ConversationPacket
				{
					Data = packet,
					SubOpcode = cols.SubOpcode,
					Env = cols.Env,
					Field = cols.Field,
					Flag = cols.Flag,
					ScreensJson = cols.ScreensJson
				};

				var fromColumns = ConversationService.BuildKnubotFromColumns(p, LiveInstance, ClickedNpc, "", "");
				Assert.True(ConversationService.TryConstructKnubotRecord(packet, LiveInstance, ClickedNpc, "", "", out var fromData));
				Assert.Equal(Convert.ToHexStringLower(fromData), Convert.ToHexStringLower(fromColumns));
			}
		}

		[Fact]
		public void Construct_AnswerList_IsByteIdenticalToSubstitution()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.AnswerListBody);
			const uint capturedInst = 0x0802E5D4, capturedNpc = 0x00031815;

			var p = KnubotCodec.ParseStringList(captured);
			var construct = KnubotCodec.BuildAnswerList(LiveInstance, ClickedNpc, p.Strings, p.Env);
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_AppendText_IsByteIdenticalToSubstitution()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.AppendTextBody);

			// construct: parse the captured record, rebuild with the live endpoints
			var p = KnubotCodec.ParseAppendText(captured);
			var construct = KnubotCodec.BuildAppendText(LiveInstance, ClickedNpc, p.Xml, p.Field, p.Trailing, p.Env);

			// replay: substitute the two handles in the captured record
			var substitute = SubstituteU32(SubstituteU32(captured, CapturedInstance, LiveInstance), CapturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_CloseChatWindow_IsByteIdenticalToSubstitution()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.CloseChatWindowBody);
			const uint capturedInst = 0x01316D8A, capturedNpc = 0x00002C7C;

			var p = KnubotCodec.ParseStringList(captured);
			var construct = KnubotCodec.BuildCloseChatWindow(LiveInstance, ClickedNpc, p.Strings, p.Env);
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_OpenChatWindow_IsByteIdenticalToSubstitution()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.OpenChatWindowBody);
			const uint capturedInst = 0x01316D8A, capturedNpc = 0x00002C7C;

			var p = KnubotCodec.ParseOpenChatWindow(captured);
			var construct = KnubotCodec.BuildOpenChatWindow(LiveInstance, ClickedNpc, p.Value, p.Env);
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		// Retail varies the GameAgent sender instance per conversation (0x07..0x5a in the captures); we preserve the
		// captured packet's instance so the constructed framing matches retail byte-for-byte. ReadSenderInstance must
		// recover exactly what the real endpoint encoder (Endpoints) wrote.
		[Theory, InlineData(0x08u), InlineData(0x0Eu), InlineData(0x5Au), InlineData(0x01316E71u)]
		// a multi-byte varint instance, for good measure
		public void ReadSenderInstance_RecoversTheEncodedGameAgentInstance(uint instance)
		{
			var sender = Endpoints.EndpointBytes(Endpoints.GameAgentType, instance);

			// assemble a header the way PacketStream.WriteHeader does: [8 len+crc][u32 headerLen][0x0a][byteLen][sender]...
			var packet = new byte[14 + sender.Length + 1];
			packet[12] = 0x0a;
			packet[13] = (byte)sender.Length;
			Buffer.BlockCopy(sender, 0, packet, 14, sender.Length);

			Assert.Equal(instance, ConversationService.ReadSenderInstance(packet));
		}

		[Fact]
		public void TryConstructKnubotRecord_LiftsAndRebuildsFromAFullPacket()
		{
			var record = Convert.FromHexString(KnubotCodecTestData.AppendTextBody);

			// wrap the record in a full conversation packet: [8 len+crc][u32 headerLen][header][record]
			var header = new byte[20];
			var packet = new byte[12 + header.Length + record.Length];
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), (uint)header.Length);
			Buffer.BlockCopy(header, 0, packet, 12, header.Length);
			Buffer.BlockCopy(record, 0, packet, 12 + header.Length, record.Length);

			Assert.True(ConversationService.TryConstructKnubotRecord(packet, LiveInstance, ClickedNpc, "", "", out var rebuilt));

			var expected = KnubotCodec.BuildAppendText(LiveInstance, ClickedNpc, KnubotCodec.ParseAppendText(record).Xml);
			Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void TryConstructKnubotRecord_RebuildsAnswerListAndCloseChatWindow()
		{
			foreach (var recordHex in new[] { KnubotCodecTestData.AnswerListBody, KnubotCodecTestData.CloseChatWindowBody })
			{
				var record = Convert.FromHexString(recordHex);
				var packet = WrapInPacket(record);
				Assert.True(ConversationService.TryConstructKnubotRecord(packet, LiveInstance, ClickedNpc, "", "", out var rebuilt));
				var sub = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4)); // preserve AnswerList vs CloseChatWindow
				var p = KnubotCodec.ParseStringList(record);
				var expected = KnubotCodec.BuildStringList(sub, LiveInstance, ClickedNpc, p.Strings, p.Env);
				Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(rebuilt));
			}
		}

		[Fact]
		public void TryConstructKnubotRecord_ReturnsFalseForUnhandledSubOpcode()
		{
			// a non-constructed sub-opcode (here a quest QuestLogList 0x504B7924) must stay on the substitution path
			var record = Convert.FromHexString("0000000c504b79240000c350110099bd00");
			var packet = WrapInPacket(record);
			Assert.False(ConversationService.TryConstructKnubotRecord(packet, LiveInstance, ClickedNpc, "", "", out _));
		}

		// Replaces every big-endian u32 occurrence of oldVal with newVal - the record-level equivalent of
		// ConversationService.SubstituteUInt32 (which scans from offset 8 of a FULL packet; a bare record's handles
		// sit past that, so scanning the whole record matches).
		private static byte[] SubstituteU32(byte[] data, uint oldVal, uint newVal)
		{
			var outp = (byte[])data.Clone();
			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, oldVal);
			for (var i = 0; i + 4 <= outp.Length; i++)
			{
				if (outp.AsSpan(i, 4).SequenceEqual(needle))
				{
					BinaryPrimitives.WriteUInt32BigEndian(outp.AsSpan(i, 4), newVal);
					i += 3;
				}
			}

			return outp;
		}

		private static byte[] WrapInPacket(byte[] record)
		{
			var header = new byte[12];
			var packet = new byte[12 + header.Length + record.Length];
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), (uint)header.Length);
			Buffer.BlockCopy(record, 0, packet, 12 + header.Length, record.Length);
			return packet;
		}
	}
}
