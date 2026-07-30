using System;
using System.Text;
using Faolan.Core.Network.Codecs.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the Knubot conversation records (#188,
	// docs/re/iir-readers-conversation-misc.md): KnubotAppendText (the NPC dialogue line, a remoteformat XML payload),
	// KnubotOpenChatWindow, and the AnswerList/CloseChatWindow string-list pair. Proves the nested-endpoint carrier
	// (player = carrier handle, NPC in the KnubotBase header) reconstructs byte-exact.
	//
	// ⚠️ The captured oracles are the whole carrier payload, which BEGINS with the [u32 len]. That word belongs to
	// the sender now (#530 rule 2), so every assertion here is against `Record(...)` - the capture minus its first
	// four bytes. Nothing on the wire moved: ConversationService writes the same length back on.
	public class KnubotCodecTests
	{
		// the captured s2c CloseChatWindow (an answer-list of one empty remoteformat id="0")
		private const string CloseChatWindowBody =
			"0000007cc4af39eb0000c35001316e710000010000c35000017e0300000001005f3c72656d6f7465666f726d61742069643d2230222063617465676f72793d2231303022206b65793d222e667a5475457969753742262339323b5a595a4d6f71312d22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e";

		private const string OpenChatWindowBody = "00000018045b0b0f0000c35001316e710000010000c35000017e0300";

		// AnswerList + CloseChatWindow share the [u32 count][ count x string ] grammar.
		[Fact]
		public void AnswerList_BuildsByteExact()
		{
			var p = (KnubotAnswerListIIR)KnubotCodec.Parse(Record(KnubotCodecTestData.AnswerListBody));

			Assert.Equal(0x0802E5D4u, p.PlayerInstance);
			Assert.Equal(0x00031815u, p.NpcHandle);
			Assert.Single(p.Strings);
			Assert.Equal(RecordHex(KnubotCodecTestData.AnswerListBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		[Fact]
		public void AppendText_BuildsByteExact()
		{
			var p = (KnubotAppendTextIIR)KnubotCodec.Parse(Record(KnubotCodecTestData.AppendTextBody));

			Assert.Equal(0x110099BDu, p.PlayerInstance);
			Assert.Equal(0x00042194u, p.NpcHandle);
			Assert.StartsWith("<remoteformat", Encoding.ASCII.GetString(p.Xml));
			Assert.Equal(RecordHex(KnubotCodecTestData.AppendTextBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		// The captured greeting's XML (#188 construct path): building it for the captured endpoints must reproduce the
		// captured AppendText body byte-for-byte. The XML oracle now lives in the test project (KnubotCodecTestData);
		// the runtime serves real dialogue from the conversation DB (ConversationService), not a baked sample.
		[Fact]
		public void CapturedGreetingXml_BuildsTheCapturedLine()
		{
			var rebuilt = KnubotCodec.Build(new KnubotAppendTextIIR(0x110099BDu, 0x00042194u, 0x00, 1, KnubotCodecTestData.RemoteFormatXml, 1));

			Assert.Equal(RecordHex(KnubotCodecTestData.AppendTextBody), Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void CloseChatWindow_BuildsByteExact()
		{
			var p = (KnubotCloseChatWindowIIR)KnubotCodec.Parse(Record(CloseChatWindowBody));

			Assert.Single(p.Strings);
			Assert.StartsWith("<remoteformat id=\"0\"", Encoding.ASCII.GetString(p.Strings[0]));
			Assert.Equal(RecordHex(CloseChatWindowBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		[Fact]
		public void OpenChatWindow_BuildsByteExact()
		{
			var p = (KnubotOpenChatWindowIIR)KnubotCodec.Parse(Record(OpenChatWindowBody));

			Assert.Equal(0x01316E71u, p.PlayerInstance);
			Assert.Equal(0x00017E03u, p.NpcHandle);
			Assert.Equal(RecordHex(OpenChatWindowBody), Convert.ToHexStringLower(KnubotCodec.Build(p)));
		}

		// The captured payload minus the carrier's [u32 len], which is the sender's (#530 rule 2).
		internal static byte[] Record(string capturedHex)
		{
			return Convert.FromHexString(RecordHex(capturedHex));
		}

		internal static string RecordHex(string capturedHex)
		{
			return capturedHex[8..];
		}
	}
}
