using System;
using Faolan.Core.Network.Codecs.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exactness guard for the ScryScriptIIR (0xA36D3B74) builders, added when they moved off a hand-written
	// protobuf writer onto the generated ProtoScryScriptIIR (Gitea #386 sweep). The expected bodies are what the
	// hand-written writer produced, so these pin the conversion rather than merely re-asserting the new code.
	//
	// The interesting one is field 3. It is a sint32 (ZIGZAG), but the callers pass the RAW WIRE VARINT - the
	// values came straight out of captures. BuildStartScriptGroup(_, 48126) must therefore still put 48126 on the
	// wire (`18 FE F7 02`), not zigzag(48126). Across the corpus field 3 is even 1151 times and odd 14 - the
	// signature of zigzag over mostly-positive values - so 48126 really denotes int_parameter 24063.
	public class ScryScriptCodecTests
	{
		private const uint Handle = 0x0802E5D4;

		[Fact]
		public void BuildStartScriptGroup_MatchesTheHandWrittenBytes()
		{
			// ...0E 08 05 10 00 18 FEF702 32 04 08 00 10 00 - version 5, type 0, the raw 48126, id_parameter {0,0}.
			Assert.Equal(
				"A36D3B740000C3500802E5D4000000000E0805100018FEF702320408001000",
				Convert.ToHexString(ScryScript.BuildStartScriptGroup(Handle, 48126)));
		}

		[Fact]
		public void BuildStep_SimpleToken_MatchesTheHandWrittenBytes()
		{
			Assert.Equal(
				"A36D3B740000C3500802E5D400000000190805100218002209496E74726F446F6E652A00320408001000",
				Convert.ToHexString(ScryScript.BuildStep(Handle, "IntroDone")));
		}

		[Fact]
		public void BuildStep_RichCreationStep_MatchesTheHandWrittenBytes()
		{
			// mode 7 + a resource list + a non-zero id_parameter pair (50000, 0x2c1c).
			Assert.Equal(
				"A36D3B740000C3500802E5D4000000003008051007180022164E45575F6368617261637465725F6372656174696F6E2A07313A323B333A34320708D08603109C58",
				Convert.ToHexString(ScryScript.BuildStep(Handle, "NEW_character_creation", 7, 0, "1:2;3:4", 50000, 0x2c1c)));
		}
	}
}
