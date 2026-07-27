using System;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// PlayfieldFIIR / MapChange (0x5D85BFC7). The BODY builder has had a golden since #189
	// (CreateEditorRecordTests.PlayfieldFiir_MatchesCapturedBytes); what was never pinned is the CARRIER - the
	// [u32 subop][body] wrapper MapChangeCodec puts around it, and the offsets its parser reads back out. Both halves
	// together are what actually goes on the wire, so this compares them against a whole captured record.
	//
	// ⚠️ A pass here does NOT mean the record is fully modelled. Across the 151 captured PlayfieldFIIRs, FOUR body
	// bytes vary that BuildPlayfieldFiir writes as constants - @33, @58, @86 and @98. Two of them (@33 and @98) are
	// per-MAP: every record for a given playfield carries the same value, and ours are wrong for six of the twelve
	// captured maps. This golden uses a capture whose four bytes happen to equal our constants, which is exactly why
	// it can pass while the gap is open. Gitea #451.
	public class MapChangeCodecTests
	{
		// Blablabla_soldier-guardian_create frame 1926 (s2c): the whole IIR content past the carrier's [u32 length],
		// i.e. [u32 subop][103-byte PlayfieldFiir body]. Map 4000 (Stygian Galley), the create flow's first MapChange.
		private const string CapturedIirContent =
			"5d85bfc7" +
			"000000000000000000438200000000000043910000620000c79c00000fa0000000000000000000000000000000010000" +
			"9c5001ea7b9c0000000000000000180000000101ea7b9c0000cb2041700000415dc69500000000ffffffffffffffff00" +
			"00000700000080";

		private static byte[] BuildCapturedBody()
		{
			return CreateEditorRecordBuilder.BuildPlayfieldFiir(260f, 0f, 290f, 0x00000FA0, 0x00000000, 1, 0x01EA7B9C, 15f, 13.860981941223145f, 0x00000080);
		}

		[Fact]
		public void BuildIir_ReproducesTheCapturedRecord()
		{
			var built = MapChangeCodec.BuildIir(BuildCapturedBody());

			Assert.Equal(CapturedIirContent, Convert.ToHexString(built).ToLowerInvariant());
		}

		// The parser reads mapId and PlayfieldFlags by hardcoded offsets into the body, so it can drift away from the
		// builder silently. Round-tripping the captured record through both is what stops that.
		[Fact]
		public void TryParse_ReadsBackWhatTheBuilderWrote()
		{
			Assert.True(MapChangeCodec.TryParse(Convert.FromHexString(CapturedIirContent), out var parsed));

			Assert.Equal(0x00000FA0u, parsed!.MapId);
			Assert.Equal(0x00000080u, parsed.Flags);
		}

		[Fact]
		public void TryParse_RejectsANonMapChangeAndAShortBody()
		{
			Assert.False(MapChangeCodec.TryParse(Convert.FromHexString("0eed7068" + new string('0', 200)), out _)); // ChainKillIIR's subop
			Assert.False(MapChangeCodec.TryParse(Convert.FromHexString("5d85bfc7000000"), out _));
		}
	}
}
