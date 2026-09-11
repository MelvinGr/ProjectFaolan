using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// PlayfieldFIIR / MapChange (0x5D85BFC7). The body builder has its own golden
	// (CreateEditorRecordTests.PlayfieldFiir_MatchesCapturedBytes); what this pins is the carrier - the
	// [u32 subop][body] wrapper PlayfieldFCodec puts around it, and what its parser reads back out. Both halves
	// together are what actually goes on the wire, so this compares them against a whole captured record.
	//
	// A pass here does not mean the record is fully modelled. Across the 151 captured PlayfieldFIIRs, four body
	// words vary that BuildPlayfieldFiir wrote as constants. The two per-map ones are now imported and sent from the
	// Maps table (PlayfieldFieldA @30 and PlayfieldFieldC @95, which a single constant gets wrong for six of the
	// twelve captured maps). The other two - field58 @58 and the blob's last word @83 - vary within a map, so they are
	// per-entry state, supplied by the caller as constants and still undecoded. Open work tracked in Gitea #451.
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

		// Parse reads the record sequentially to its last byte, so a builder/parser drift cannot hide.
		[Fact]
		public void Parse_ReadsBackWhatTheBuilderWrote()
		{
			var parsed = PlayfieldFCodec.Parse(Convert.FromHexString(CapturedIirContent));

			Assert.Equal(0x00000FA0u, parsed.MapId);
			Assert.Equal(0x00000080u, parsed.Flags);
			Assert.Equal(CapturedIirContent, Convert.ToHexString(PlayfieldFCodec.Build(parsed)).ToLowerInvariant());
		}

		// An instanced playfield sends zero scene entries, so the blob is its count word alone - 4 bytes rather
		// than 24, and the whole record 83 bytes rather than 103. Refused before the fix, and the refusal did not stay
		// contained in the codec: NpcSpawnExtractor kept the previous map on an unreadable MapChange, so White Sands
		// Night's NPCs were harvested as City of Tortage Night spawns while the row counts looked healthy.
		// xxx_mage-necromancer_login_play4 frame 48 - the login straight into White Sands Night (10803).
		[Fact]
		public void Parse_ReadsAZeroSceneEntryBlob()
		{
			var captured = Convert.FromHexString(
				"5d85bfc700000000000000000043fa42f64257f1d244530381620000c7a600002a3300000000007c7568000000000000000100009c5001ef143e00000000000000000400000000ffffffffffffffff0000000700000000");

			var parsed = PlayfieldFCodec.Parse(captured);

			Assert.Equal(10803u, parsed.MapId); // White Sands Night
			Assert.Equal(0u, parsed.SceneEntries);
			Assert.Equal(7u, parsed.FieldC);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(PlayfieldFCodec.Build(parsed)));
		}

		[Fact]
		public void Parse_RejectsANonMapChangeAndAShortBody()
		{
			Assert.Throws<CodecParseException>(() => PlayfieldFCodec.Parse(Convert.FromHexString("0eed7068" + new string('0', 200)))); // ChainKillIIR's subop
			Assert.Throws<CodecParseException>(() => PlayfieldFCodec.Parse(Convert.FromHexString("5d85bfc7000000")));
		}
	}
}
