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

		[Fact]
		public void ComboBookIir_MatchesCapturedBytes()
		{
			// First ComboBook of the WorldPrep stream (handle 0x02000001, combo id 0x00416544).
			Assert.Equal("0000c35002000001000100416544",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildComboBookIir(0x02000001, 0x00416544)).ToLowerInvariant());
		}

		[Fact]
		public void StatUpdateIir_MatchesCapturedBytes()
		{
			// First StatUpdate (handle 0x0009FF92, 3 pairs) and the 6-pair record (handle 0x02000001).
			Assert.Equal("0000c3500009ff9200000000030000001b0000189c000001f80000189c000001fa0000189c",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF92,
					[(0x1B, 0x189C), (0x1F8, 0x189C), (0x1FA, 0x189C)])).ToLowerInvariant());
			Assert.Equal("0000c3500200000100000000060000001b00001f40000000ba00000000000001ff00000000000002000000030100000201000000000000025f59ea5c54",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildStatUpdateIir(0x02000001,
					[(0x1B, 0x1F40), (0xBA, 0), (0x1FF, 0), (0x200, 0x0301), (0x201, 0), (0x25F, 0x59EA5C54)])).ToLowerInvariant());
		}

		[Fact]
		public void SetMorphValueIir_MatchesCapturedBytes()
		{
			// hairmesh resource (handle 0x0009FF94, weight 1.0, p=0 q=1) and a texscript (p=1 q=1).
			Assert.Equal("0000c3500009ff940000102a686169726d6573685f3230373932303f8000000000000001",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF94, "hairmesh_207920", 1.0f, 0x00, 0x00000001)).ToLowerInvariant());
			Assert.Equal("0000c350020000010000112a7465787363726970745f3138333837353f8000000100000001",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildSetMorphValueIir(0x02000001, "texscript_183875", 1.0f, 0x01, 0x00000001)).ToLowerInvariant());
		}

		[Fact]
		public void CharacterActionIir_MatchesCapturedBytes()
		{
			// First + last CharacterActionIIR of the WorldPrep stream (handle 0x02000001): grants action ids
			// 0x012C and 0x050E24 (decode_character_action.py validated all 26 records).
			Assert.Equal(
				"0000c35002000001000000001d000000000000000000000000000000000000012c0000000000000000",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildCharacterActionIir(0x02000001, 0x0000012C)).ToLowerInvariant());
			Assert.Equal(
				"0000c35002000001000000001d0000000000000000000000000000000000050e240000000000000000",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildCharacterActionIir(0x02000001, 0x00050E24)).ToLowerInvariant());
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
