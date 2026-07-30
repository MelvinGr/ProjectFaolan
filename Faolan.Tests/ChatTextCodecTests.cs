using System;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the ChatTextIIR (0xE0B51102) body - a world/system chat line the server
	// pushes into the client's chat window (#103). Oracle: the localized CriteriaYou system line from the DemoL80
	// randomActions capture (#7564). Proves the codec reconstructs the captured bytes from the parsed fields
	// (construct-don't-replay) and that Parse is its faithful inverse.
	public class ChatTextCodecTests
	{
		// ChatTextIIR system line: a remoteformat (id 562) with two localized <parameter>s (CriteriaYou + localized
		// id 700). instance 0x110099bd = the player's GameInterface object.
		private const string CriteriaLineBody = "000001abe0b511020000c350110099bd0001973c72656d6f7465666f726d61742069643d22353632222063617465676f72793d" +
		                                        "2231303022206b65793d2236736567534b2f55493f4b6360547a672e74696f22206b6e75626f743d22302220203e3c70617261" +
		                                        "6d657465722076616c75653d22373b266c743b6c6f63616c697a656420746f6b656e3d2671756f743b4372697465726961596f" +
		                                        "752671756f743b2063617465676f72793d2671756f743b3133302671756f743b20666c6167733d2671756f743b2671756f743b" +
		                                        "206b65793d2671756f743b5e344c3226616d703b2333363b64553541476a6354585f784a5b2a512671756f743b202667743b22" +
		                                        "202f3e3c706172616d657465722076616c75653d22373b266c743b6c6f63616c697a65642069643d2671756f743b3730302671" +
		                                        "756f743b2063617465676f72793d2671756f743b35353230302671756f743b20666c6167733d2671756f743b2671756f743b20" +
		                                        "6b65793d2671756f743b4d7052662e43782e255e4d476446585426616d703b71756f743b3958282671756f743b202667743b22" +
		                                        "202f3e3c2f72656d6f7465666f726d61743e0040000001";

		// The record now LEADS with its sub-opcode: the carrier's [u32 len] is the sender's (#530 rule 2), written by
		// WorldIir.Send. So every captured oracle here is compared against `Record(...)` - the capture minus its
		// first four bytes. No wire byte moved.
		[Fact]
		public void Build_FromArbitraryXml_LeadsWithTheSubopcode()
		{
			var xml = Encoding.ASCII.GetBytes("<remoteformat id=\"0\" ></remoteformat>");
			var body = ChatTextCodec.Build(0x00ABCDEFu, xml);

			Assert.Equal(ChatTextCodec.Opcode, body.ReadU32Be(0));
			Assert.Equal(0x00ABCDEFu, ChatTextCodec.Parse(body).Instance);
		}

		[Fact]
		public void Build_FromParsedFields_MatchesCapture()
		{
			var p = ChatTextCodec.Parse(Record(CriteriaLineBody));

			var rebuilt = ChatTextCodec.Build(p);

			Assert.Equal(RecordHex(CriteriaLineBody), Convert.ToHexStringLower(rebuilt));
		}

		private static byte[] Record(string capturedHex)
		{
			return Convert.FromHexString(RecordHex(capturedHex));
		}

		private static string RecordHex(string capturedHex)
		{
			return capturedHex[8..];
		}

		[Fact]
		public void Parse_DecodesTheSystemLine()
		{
			var p = ChatTextCodec.Parse(Record(CriteriaLineBody));

			Assert.Equal(0x110099bdu, p.Instance);
			var xml = Encoding.ASCII.GetString(p.Xml);
			Assert.StartsWith("<remoteformat", xml);
			Assert.Contains("CriteriaYou", xml);
		}
	}
}
