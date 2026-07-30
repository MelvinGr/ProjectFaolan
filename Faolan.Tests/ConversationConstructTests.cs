using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
			Assert.Equal(ConversationSubOpcode.SetDialogueCamera, cols.SubOpcode);
			Assert.Equal(0x58u, cols.SenderInstance);

			// The captured body's two handles: [0xC350][npc][0xC350][player] at 12 + headerLen.
			var body = 12 + (int)captured.ReadU32Be(8);
			var npc = captured.ReadU32Be(body + 4);
			var player = captured.ReadU32Be(body + 12);

			var rebuilt = ConversationService.BuildDialogueCameraPacket(cols.SenderInstance!.Value, player, npc, ConversationService.CameraArgsOf(new ConversationPacket
			{
				ShotSize = cols.Camera!.ShotSize, ShotType = cols.Camera.ShotType, ShotEmphasis = cols.Camera.ShotEmphasis, ShotFov = cols.Camera.ShotFov,
				ShotTilt = cols.Camera.ShotTilt, Autoface = cols.Camera.Autoface, HeadPaddingY = cols.Camera.HeadPaddingY
			})).Serialize();
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(rebuilt));
		}

		// The structured-column construct (#188) must be BYTE-IDENTICAL to the direct codec construct from the raw
		// record, so building the packet from the typed columns (no Data blob) doesn't change the wire output.
		[Fact]
		public void BuildFromColumns_MatchesTheCodecConstruct()
		{
			foreach (var recordHex in new[] { KnubotCodecTestData.AppendTextBody, KnubotCodecTestData.AnswerListBody, KnubotCodecTestData.CloseChatWindowBody })
			{
				var record = Convert.FromHexString(recordHex);
				var packet = WrapInPacket(record);

				var cols = ConversationScreenDecoder.Decode(packet);
				Assert.NotNull(cols);
				var p = new ConversationPacket
				{
					SubOpcode = cols.SubOpcode,
					Env = cols.Env,
					Field = cols.Field,
					Flag = cols.Flag
				};
				for (var i = 0; i < cols.Screens!.Count; i++)
				{
					var screen = ConversationScreenCodec.Parse(cols.Screens[i]);
					screen.Sequence = i;
					p.Screens.Add(screen);
				}

				var fromColumns = ConversationService.BuildKnubotFromColumns(p, LiveInstance, ClickedNpc, "", "");
				Assert.Equal(Convert.ToHexStringLower(ExpectedConstruct(record[4..])), Convert.ToHexStringLower(fromColumns));
			}
		}

		[Fact]
		public void Construct_AnswerList_IsByteIdenticalToSubstitution()
		{
			var captured = KnubotCodecTests.Record(KnubotCodecTestData.AnswerListBody);
			const uint capturedInst = 0x0802E5D4, capturedNpc = 0x00031815;

			var p = (KnubotAnswerListIIR)KnubotCodec.Parse(captured);
			var construct = KnubotCodec.Build(p with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc });
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_AppendText_IsByteIdenticalToSubstitution()
		{
			var captured = KnubotCodecTests.Record(KnubotCodecTestData.AppendTextBody);

			// construct: parse the captured record, rebuild with the live endpoints
			var p = (KnubotAppendTextIIR)KnubotCodec.Parse(captured);
			var construct = KnubotCodec.Build(p with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc });

			// replay: substitute the two handles in the captured record
			var substitute = SubstituteU32(SubstituteU32(captured, CapturedInstance, LiveInstance), CapturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_CloseChatWindow_IsByteIdenticalToSubstitution()
		{
			var captured = KnubotCodecTests.Record(KnubotCodecTestData.CloseChatWindowBody);
			const uint capturedInst = 0x01316D8A, capturedNpc = 0x00002C7C;

			var p = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(captured);
			var construct = KnubotCodec.Build(p with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc });
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		[Fact]
		public void Construct_OpenChatWindow_IsByteIdenticalToSubstitution()
		{
			var captured = KnubotCodecTests.Record(KnubotCodecTestData.OpenChatWindowBody);
			const uint capturedInst = 0x01316D8A, capturedNpc = 0x00002C7C;

			var p = (KnubotOpenChatWindowIIR)KnubotCodec.Parse(captured);
			var construct = KnubotCodec.Build(p with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc });
			var substitute = SubstituteU32(SubstituteU32(captured, capturedInst, LiveInstance), capturedNpc, ClickedNpc);

			Assert.Equal(Convert.ToHexStringLower(substitute), Convert.ToHexStringLower(construct));
		}

		// Retail varies the GameAgent sender instance per conversation (0x07..0x5a in the captures); the decoder now
		// recovers it into ConversationScreen.SenderInstance so the send path re-frames from columns (no raw Data).
		// Verify the decode recovers exactly what the real endpoint encoder (Endpoints) wrote, incl. a multi-byte varint.
		[Theory, InlineData(0x08u), InlineData(0x5Au), InlineData(0x01316E71u)]
		public void Decode_RecoversTheSenderInstance(uint instance)
		{
			// A real CloseChatWindow record wrapped in a packet whose rpcHeader carries the chosen GameAgent sender:
			// [8 len+crc][u32 headerLen][0x0a][byteLen][sender endpoint][record].
			var record = Convert.FromHexString(KnubotCodecTestData.CloseChatWindowBody);
			var sender = Endpoints.EndpointBytes(Endpoints.GameAgentType, instance);
			var header = new byte[2 + sender.Length];
			header[0] = 0x0a;
			header[1] = (byte)sender.Length;
			Buffer.BlockCopy(sender, 0, header, 2, sender.Length);

			var packet = new byte[12 + header.Length + record.Length];
			packet.WriteU32Be(8, (uint)header.Length);
			Buffer.BlockCopy(header, 0, packet, 12, header.Length);
			Buffer.BlockCopy(record, 0, packet, 12 + header.Length, record.Length);

			var cols = ConversationScreenDecoder.Decode(packet);
			Assert.NotNull(cols);
			Assert.Equal(instance, cols.SenderInstance);
		}

		// The codec-construct oracle: rebuild the record straight from the parsed raw record (the path KnubotCodecTests
		// gates against the captures), for comparison with the from-columns build. Takes the record WITHOUT the
		// carrier's [u32 len], which is the sender's now (#530 rule 2).
		private static byte[] ExpectedConstruct(byte[] record)
		{
			var parsed = KnubotCodec.Parse(record);

			return KnubotCodec.Build(parsed switch
			{
				KnubotAppendTextIIR a => a with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc },
				KnubotAnswerListIIR l => l with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc },
				KnubotCloseChatWindowIIR c => c with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc },
				KnubotOpenChatWindowIIR o => o with { PlayerInstance = LiveInstance, NpcHandle = ClickedNpc },
				_ => throw new InvalidOperationException("not a Knubot record")
			});
		}

		// Replaces every big-endian u32 occurrence of oldVal with newVal - the record-level handle substitution the
		// Construct_* tests compare the codec build against (a bare record's handles sit past the length prefix).
		private static byte[] SubstituteU32(byte[] data, uint oldVal, uint newVal)
		{
			var outp = (byte[])data.Clone();
			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, oldVal);
			for (var i = 0; i + 4 <= outp.Length; i++)
			{
				if (outp.AsSpan(i, 4).SequenceEqual(needle))
				{
					outp.WriteU32Be(i, newVal);
					i += 3;
				}
			}

			return outp;
		}

		private static byte[] WrapInPacket(byte[] record)
		{
			var header = new byte[12];
			var packet = new byte[12 + header.Length + record.Length];
			packet.WriteU32Be(8, (uint)header.Length);
			Buffer.BlockCopy(record, 0, packet, 12 + header.Length, record.Length);
			return packet;
		}
	}
}
