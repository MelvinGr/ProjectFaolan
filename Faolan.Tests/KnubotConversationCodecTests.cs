using System;
using System.Text;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the four Knubot conversation IIRs (#188): OpenChatWindow,
	// AppendText, AnswerList, CloseChatWindow. The small OpenChatWindow/CloseChatWindow records are real oracles
	// (smallest of their type from the captured ConversationPackets); the larger AppendText/AnswerList grammars
	// (the field + trailing flag, and the answer-count loop) are exercised by constructed round-trips - their
	// byte-exactness against capture is already gated by Captures/scripts/decode_knubot.py (307 rows + 565 packets).
	public class KnubotConversationCodecTests
	{
		// Real OpenChatWindow record (flag 0x00): instance 0x02165eff, convId 0x0004de8a.
		private const string OpenRecord =
			"00000018045b0b0f0000c35002165eff0000010000c3500004de8a00";

		// Real CloseChatWindow record: one farewell remoteformat screen, field=1.
		private const string CloseRecord =
			"0000007cc4af39eb0000c35002165eff0000010000c3500004de8a00000001005f3c72656d6f7465666f726d61742069643d2230" +
			"222063617465676f72793d2231303022206b65793d222e667a5475457969753742262339323b5a595a4d6f71312d22206b6e7562" +
			"6f743d22302220203e3c2f72656d6f7465666f726d61743e";

		[Theory]
		[InlineData(OpenRecord, KnubotConversationCodec.OpenChatWindow)]
		[InlineData(CloseRecord, KnubotConversationCodec.CloseChatWindow)]
		public void Build_FromParsedFields_MatchesCapture(string hex, uint expectedSubop)
		{
			var captured = Convert.FromHexString(hex);
			var p = KnubotConversationCodec.Parse(captured);
			Assert.Equal(expectedSubop, p.SubOpcode);

			var rebuilt = p.SubOpcode == KnubotConversationCodec.OpenChatWindow
				? KnubotConversationCodec.BuildOpen(p.Instance, p.ConvId, p.Flag!.Value)
				: KnubotConversationCodec.BuildCloseChatWindow(p.Instance, p.ConvId, p.Screens[0], p.Field!.Value);

			Assert.Equal(hex, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Parse_Open_HasSharedHeaderAndFlag()
		{
			var p = KnubotConversationCodec.Parse(Convert.FromHexString(OpenRecord));
			Assert.Equal(KnubotConversationCodec.OpenChatWindow, p.SubOpcode);
			Assert.Equal(0x02165effu, p.Instance);
			Assert.Equal(0x0004de8au, p.ConvId);
			Assert.Equal((byte)0x00, p.Flag);
			Assert.Empty(p.Screens);
		}

		[Fact]
		public void Parse_Close_HasOneScreen()
		{
			var p = KnubotConversationCodec.Parse(Convert.FromHexString(CloseRecord));
			Assert.Equal(KnubotConversationCodec.CloseChatWindow, p.SubOpcode);
			Assert.Equal(1u, p.Field);
			Assert.Single(p.Screens);
			Assert.StartsWith("<remoteformat", Encoding.ASCII.GetString(p.Screens[0]));
		}

		[Fact]
		public void AppendText_RoundTrips_WithFieldAndFlag()
		{
			var xml = Encoding.ASCII.GetBytes("<remoteformat id=\"33600\" ></remoteformat>");
			var record = KnubotConversationCodec.BuildAppendText(0x110099bdu, 0x00042194u, xml, field: 6, flag: 1);

			var p = KnubotConversationCodec.Parse(record);
			Assert.Equal(KnubotConversationCodec.AppendText, p.SubOpcode);
			Assert.Equal(0x110099bdu, p.Instance);
			Assert.Equal(0x00042194u, p.ConvId);
			Assert.Equal(6u, p.Field);
			Assert.Equal((byte)1, p.Flag);
			Assert.Single(p.Screens);
			Assert.Equal(xml, p.Screens[0]);
			// Build is Parse's faithful inverse.
			Assert.Equal(Convert.ToHexStringLower(record),
				Convert.ToHexStringLower(KnubotConversationCodec.BuildAppendText(
					p.Instance, p.ConvId, p.Screens[0], p.Field!.Value, p.Flag!.Value)));
		}

		[Fact]
		public void AnswerList_RoundTrips_MultipleAnswers()
		{
			var answers = new[]
			{
				Encoding.ASCII.GetBytes("<remoteformat id=\"1\" ></remoteformat>"),
				Encoding.ASCII.GetBytes("<remoteformat id=\"2\" ></remoteformat>"),
				Encoding.ASCII.GetBytes("<remoteformat id=\"3\" ></remoteformat>"),
			};
			var record = KnubotConversationCodec.BuildAnswerList(0x0802e5d4u, 0x00031815u, answers);

			var p = KnubotConversationCodec.Parse(record);
			Assert.Equal(KnubotConversationCodec.AnswerList, p.SubOpcode);
			Assert.Equal(0x0802e5d4u, p.Instance);
			Assert.Equal(3, p.Screens.Count);
			Assert.Equal((uint)(record.Length - 4),
				System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(0, 4)));
			Assert.Equal(Convert.ToHexStringLower(record),
				Convert.ToHexStringLower(KnubotConversationCodec.BuildAnswerList(p.Instance, p.ConvId, p.Screens)));
		}
	}
}
