using System;
using System.Text;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the Knubot conversation records
	// (docs/re/iir-readers-conversation-misc.md): KnubotAppendText (the NPC dialogue line, a remoteformat XML payload),
	// KnubotOpenChatWindow, and the AnswerList/CloseChatWindow string-list pair. Proves the nested-endpoint carrier
	// (player = carrier handle, NPC in the KnubotBase header) reconstructs byte-exact.
	//
	// The captured oracles are the whole carrier payload, which begins with the [u32 len]. That word belongs to
	// the sender, so every assertion here is against `CapturedRecord.Bytes(...)` - the capture minus its first four
	// bytes. ConversationService writes that length back on.
	public class KnubotCodecTests
	{
		// the captured s2c CloseChatWindow (an answer-list of one empty remoteformat id="0")
		private const string CloseChatWindowBody =
			"0000007cc4af39eb0000c35001316e710000010000c35000017e0300000001005f3c72656d6f7465666f726d61742069643d2230222063617465676f72793d2231303022206b65793d222e667a5475457969753742262339323b5a595a4d6f71312d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e";

		private const string OpenChatWindowBody = "00000018045b0b0f0000c35001316e710000010000c35000017e0300";

		// The c2s close, from the corpus. All 7 captured picks are 29 bytes: the KnubotBase header, TextType 0
		// and an empty text. Under the old "count-framed string list" reading this was a zero count with two stray
		// bytes after it, so every one of them was refused.
		private const string CloseChatWindowC2SBody = "0000001dc4af39eb0000c35001316e710000010000c3500003fc08000000000000";

		// AnswerList really is a count-framed string list; CloseChatWindow only looks like one.
		[Fact]
		public void AnswerList_BuildsByteExact()
		{
			var p = (KnubotAnswerListIIR)KnubotCodec.Parse(CapturedRecord.Bytes(KnubotCodecTestData.AnswerListBody));

			Assert.Equal(0x0802E5D4u, p.PlayerInstance);
			Assert.Equal(0x00031815u, p.NpcHandle);
			Assert.Single(p.Strings);
			Assert.Equal(CapturedRecord.Hex(KnubotCodecTestData.AnswerListBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		[Fact]
		public void AppendText_BuildsByteExact()
		{
			var p = (KnubotAppendTextIIR)KnubotCodec.Parse(CapturedRecord.Bytes(KnubotCodecTestData.AppendTextBody));

			Assert.Equal(0x110099BDu, p.PlayerInstance);
			Assert.Equal(0x00042194u, p.NpcHandle);
			Assert.StartsWith("<remoteformat", Encoding.ASCII.GetString(p.Xml));
			Assert.Equal(CapturedRecord.Hex(KnubotCodecTestData.AppendTextBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		// The captured greeting's XML: building it for the captured endpoints must reproduce the captured AppendText
		// body byte-for-byte. The XML oracle lives in the test project (KnubotCodecTestData);
		// the runtime serves real dialogue from the conversation DB (ConversationService), not a baked sample.
		[Fact]
		public void CapturedGreetingXml_BuildsTheCapturedLine()
		{
			var rebuilt = KnubotCodec.Build(new KnubotAppendTextIIR(0x110099BDu, 0x00042194u, 0x00, 1, KnubotCodecTestData.RemoteFormatXml, 1));

			Assert.Equal(CapturedRecord.Hex(KnubotCodecTestData.AppendTextBody), Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void CloseChatWindow_BuildsByteExact()
		{
			var p = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(CapturedRecord.Bytes(CloseChatWindowBody));

			Assert.Equal(1u, p.TextType);
			Assert.StartsWith("<remoteformat id=\"0\"", Encoding.ASCII.GetString(p.Text));
			Assert.Equal(CapturedRecord.Hex(CloseChatWindowBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		[Fact]
		public void CloseChatWindow_C2S_BuildsByteExact()
		{
			var p = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(CapturedRecord.Bytes(CloseChatWindowC2SBody));

			Assert.Equal(0x01316E71u, p.PlayerInstance);
			Assert.Equal(0x0003FC08u, p.NpcHandle);
			Assert.Equal(0u, p.TextType);
			Assert.Empty(p.Text);
			Assert.Equal(CapturedRecord.Hex(CloseChatWindowC2SBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		[Fact]
		public void OpenChatWindow_BuildsByteExact()
		{
			var p = (KnubotOpenChatWindowIIR)KnubotCodec.Parse(CapturedRecord.Bytes(OpenChatWindowBody));

			Assert.Equal(0x01316E71u, p.PlayerInstance);
			Assert.Equal(0x00017E03u, p.NpcHandle);
			Assert.Equal(CapturedRecord.Hex(OpenChatWindowBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		// The c2s answer pick, from the corpus. This is the shape all 586 captured picks have: 31 bytes, the
		// KnubotBase header with `env` 0 and version 1, then the option index - the same header the four s2c records
		// carry, which is why the pick belongs to this family codec rather than being read as an opaque prefix plus a
		// trailing index.
		[Fact]
		public void Answer_BuildsByteExact()
		{
			const string answerBody = "0000001b4d7d91230000c35001316e710000010000c35000017ed000000000";

			var p = (KnubotAnswerIIR)KnubotCodec.Parse(CapturedRecord.Bytes(answerBody));

			Assert.Equal(0x01316E71u, p.PlayerInstance);
			Assert.Equal(0x00017ED0u, p.NpcHandle);
			Assert.Equal(0x00, p.Env);
			Assert.Equal(0u, p.Index);
			Assert.Equal(CapturedRecord.Hex(answerBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}
	}
}
