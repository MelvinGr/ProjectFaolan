using System;
using Faolan.GameServer;
using Xunit;

namespace Faolan.Tests
{
	// Locks the fixed-grammar character-create EDITOR IIR records to the captured bytes (issue #189,
	// decoded by Captures/decode_create_editor.py, see docs/create-editor-bursts.md). These records are
	// streamed verbatim today by RetailCreateEditorInitialBurst; the byte-exact builders are the first step
	// of replacing that replay with construction. Capture: account-stricken998-character-create-no-worldenter
	// (editor avatar handle 0x021662C8).
	public class CreateEditorRecordTests
	{
		[Fact]
		public void StatIir_MatchesCapturedBytes()
		{
			// First StatIIR of the editor stream: type=C350 handle=021662C8 flag=00 statId=0x000F4266 value=0.
			var bytes = CreateEditorRecordBuilder.BuildStatIir(0x0000C350, 0x021662C8, 0x00, 0x000F4266, 0);
			Assert.Equal("0000c350021662c800000f426600000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void StatIir_FlagOne_MatchesCapturedBytes()
		{
			// A flag=1 record from the same stream: statId 0x000F4285, value 0.
			var bytes = CreateEditorRecordBuilder.BuildStatIir(0x0000C350, 0x021662C8, 0x01, 0x000F4285, 0);
			Assert.Equal("0000c350021662c801000f428500000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void EnergyIir_MatchesCapturedBytes()
		{
			// The editor's single EnergyIIR: type=C350 handle=021662C8 flag=01 value=0.
			var bytes = CreateEditorRecordBuilder.BuildEnergyIir(0x0000C350, 0x021662C8, 0x01, 0);
			Assert.Equal("0000c350021662c80100000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Theory]
		// fa0 (Stygian Galley) and faa (Tortage Island) MapChange records from the create captures.
		[InlineData(260f, 0f, 290f, 0x00000FA0u, 0x00000000u, 3u, 0x01EA984Eu, 15f, 13.949005126953125f, 0x00000080u,
			"000000000000000000438200000000000043910000620000c79c00000fa00000000000000000000000000000000300009c5001ea984e0000000000000000180000000101ea984e0000cb2041700000415f2f2000000000ffffffffffffffff0000000700000080")]
		[InlineData(322.5f, 145.3113250732422f, 704.9000244140625f, 0x00000FAAu, 0x01316E71u, 6u, 0x01EA985Cu, 15f, 12.273988723754883f, 0x00000000u,
			"00000000000000000043a1400043114fb34430399a620000c79c00000faa0000000001316e71000000000000000600009c5001ea985c0000000000000000180000000101ea985c0000cb20417000004144624200000000ffffffffffffffff0000000700000000")]
		public void PlayfieldFiir_MatchesCapturedBytes(float x, float y, float z, uint mapId, uint charInstance,
			uint count, uint token, float nearClip, float farClip, uint flags, string expected)
		{
			var bytes = CreateEditorRecordBuilder.BuildPlayfieldFiir(x, y, z, mapId, charInstance, count, token, nearClip, farClip, flags);
			Assert.Equal(expected, Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void GameTimeIir_MatchesCapturedBytes()
		{
			// First GameTimeIIR of the editor stream: dayTime=0x0147929b00000000, flag=02,
			// timestamp=0x000192926a25b9bd, rate=1.0 (3f800000 BE).
			var bytes = CreateEditorRecordBuilder.BuildGameTimeIir(0x0147929B00000000UL, 0x02, 0x000192926A25B9BDUL);
			Assert.Equal("00000000000000000147929b0000000002000192926a25b9bd3f800000", Convert.ToHexString(bytes).ToLowerInvariant());
		}
	}
}
