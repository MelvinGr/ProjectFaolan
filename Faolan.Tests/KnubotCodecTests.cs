using System;
using Faolan.GameServer.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the Knubot conversation records (#188,
	// docs/iir-readers-conversation-misc.md): KnubotAppendText (the NPC dialogue line, a remoteformat XML payload)
	// and KnubotOpenChatWindow. Oracles are captured bodies (full record incl. the 4-byte length prefix). Proves the
	// nested-endpoint carrier (player = carrier handle, NPC in the KnubotBase header) reconstructs byte-exact.
	public class KnubotCodecTests
	{
		private const string OpenChatWindowBody = "00000018045b0b0f0000c35001316e710000010000c35000017e0300";

		[Fact]
		public void OpenChatWindow_BuildsByteExact()
		{
			var p = KnubotCodec.ParseOpenChatWindow(Convert.FromHexString(OpenChatWindowBody));
			Assert.Equal(0x01316E71u, p.PlayerInstance);
			Assert.Equal(0x00017E03u, p.NpcHandle);
			Assert.Equal(OpenChatWindowBody,
				Convert.ToHexStringLower(KnubotCodec.BuildOpenChatWindow(p.PlayerInstance, p.NpcHandle, p.Value, p.Env)));
		}


		[Fact]
		public void AppendText_BuildsByteExact()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.AppendTextBody);
			var p = KnubotCodec.ParseAppendText(captured);
			Assert.Equal(0x110099BDu, p.PlayerInstance);
			Assert.Equal(0x00042194u, p.NpcHandle);
			Assert.StartsWith("<remoteformat", System.Text.Encoding.ASCII.GetString(p.Xml));
			Assert.Equal(KnubotCodecTestData.AppendTextBody,
				Convert.ToHexStringLower(KnubotCodec.BuildAppendText(p.PlayerInstance, p.NpcHandle, p.Xml, p.Field, p.Trailing, p.Env)));
		}

		// The .npcsay GM sample (#188 construct path) is the faithful XML of the captured greeting: building it for the
		// captured endpoints must reproduce the captured AppendText body byte-for-byte.
		[Fact]
		public void NpcSaySample_BuildsTheCapturedLine()
		{
			var rebuilt = KnubotCodec.BuildAppendText(0x110099BDu, 0x00042194u, Faolan.GameServer.Dialogue.KnubotSampleLine.RemoteFormatXml);
			Assert.Equal(KnubotCodecTestData.AppendTextBody, Convert.ToHexStringLower(rebuilt));
		}

		// AnswerList + CloseChatWindow share the [u32 count][ count x string ] grammar.
		[Fact]
		public void AnswerList_BuildsByteExact()
		{
			var captured = Convert.FromHexString(KnubotCodecTestData.AnswerListBody);
			var p = KnubotCodec.ParseStringList(captured);
			Assert.Equal(0x0802E5D4u, p.PlayerInstance);
			Assert.Equal(0x00031815u, p.NpcHandle);
			Assert.Single(p.Strings);
			Assert.Equal(KnubotCodecTestData.AnswerListBody,
				Convert.ToHexStringLower(KnubotCodec.BuildAnswerList(p.PlayerInstance, p.NpcHandle, p.Strings, p.Env)));
		}

		// the captured s2c CloseChatWindow (an answer-list of one empty remoteformat id="0")
		private const string CloseChatWindowBody =
			"0000007cc4af39eb0000c35001316e710000010000c35000017e0300000001005f3c72656d6f7465666f726d61742069643d2230222063617465676f72793d2231303022206b65793d222e667a5475457969753742262339323b5a595a4d6f71312d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e";

		[Fact]
		public void CloseChatWindow_BuildsByteExact()
		{
			var captured = Convert.FromHexString(CloseChatWindowBody);
			var p = KnubotCodec.ParseStringList(captured);
			Assert.Single(p.Strings);
			Assert.StartsWith("<remoteformat id=\"0\"", System.Text.Encoding.ASCII.GetString(p.Strings[0]));
			Assert.Equal(CloseChatWindowBody,
				Convert.ToHexStringLower(KnubotCodec.BuildCloseChatWindow(p.PlayerInstance, p.NpcHandle, p.Strings, p.Env)));
		}
	}
}
