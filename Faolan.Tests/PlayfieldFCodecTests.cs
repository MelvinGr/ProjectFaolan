using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// PlayfieldFIIR / MapChange (0x5D85BFC7). The BODY builder has had a golden since #189
	// (CreateEditorRecordTests.PlayfieldFiir_MatchesCapturedBytes); what was never pinned is the CARRIER - the
	// [u32 subop][body] wrapper PlayfieldFCodec puts around it, and what its parser reads back out. Both halves
	// together are what actually goes on the wire, so this compares them against a whole captured record.
	//
	// ⚠️ A pass here does NOT mean the record is fully modelled. Across the 151 captured PlayfieldFIIRs, four body
	// words vary that BuildPlayfieldFiir wrote as constants. The two PER-MAP ones are now imported and sent from the
	// Maps table (PlayfieldFieldA @30 and PlayfieldFieldC @95, which were wrong for six of the twelve captured maps).
	// The other two - field58 @58 and fieldB @83 - vary WITHIN a map, so they are per-entry state, still supplied by
	// the caller as constants and still undecoded. Gitea #451.
	//
	// This golden uses a capture whose four words equal the values the create path supplies, which is exactly why it
	// can pass while that half of the gap is open.
	public class PlayfieldFCodecTests
	{
		// Blablabla_soldier-guardian_create frame 1926 (s2c): the whole IIR content past the carrier's [u32 length],
		// i.e. [u32 subop][103-byte PlayfieldFiir body]. Map 4000 (Stygian Galley), the create flow's first MapChange.
		private const string CapturedIirContent =
			"5d85bfc7" +
			"000000000000000000438200000000000043910000620000c79c00000fa0000000000000000000000000000000010000" +
			"9c5001ea7b9c0000000000000000180000000101ea7b9c0000cb2041700000415dc69500000000ffffffffffffffff00" +
			"00000700000080";

		private static byte[] BuildCapturedRecord()
		{
			return PlayfieldFCodec.Build(new Vector3(260f, 0f, 290f), 0x00000FA0, 0x00000000, 1, 0x01EA7B9C, 15f, 13.860981941223145f, 0x00000080);
		}

		[Fact]
		public void BuildIir_ReproducesTheCapturedRecord()
		{
			var built = BuildCapturedRecord();

			Assert.Equal(CapturedIirContent, Convert.ToHexString(built).ToLowerInvariant());
		}

		// Parse reads the record SEQUENTIALLY to its last byte (#530 rule 3), so a builder/parser drift cannot hide.
		[Fact]
		public void Parse_ReadsBackWhatTheBuilderWrote()
		{
			var parsed = PlayfieldFCodec.Parse(Convert.FromHexString(CapturedIirContent));

			Assert.Equal(0x00000FA0u, parsed.MapId);
			Assert.Equal(0x00000080u, parsed.Flags);
			Assert.Equal(CapturedIirContent, Convert.ToHexString(PlayfieldFCodec.Build(parsed)).ToLowerInvariant());
		}

		[Fact]
		public void Parse_RejectsANonMapChangeAndAShortBody()
		{
			Assert.Throws<CodecParseException>(() => PlayfieldFCodec.Parse(Convert.FromHexString("0eed7068" + new string('0', 200)))); // ChainKillIIR's subop
			Assert.Throws<CodecParseException>(() => PlayfieldFCodec.Parse(Convert.FromHexString("5d85bfc7000000")));
		}
	}
}
