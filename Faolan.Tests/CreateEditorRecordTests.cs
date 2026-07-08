using System;
using Faolan.Core.Network.Codecs.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Locks the fixed-grammar character-create EDITOR IIR records to the captured bytes (issue #189,
	// decoded by Captures/scripts/decode_create_editor.py, see docs/create-editor-bursts.md). These records are
	// streamed verbatim today by RetailCreateEditorInitialBurst; the byte-exact builders are the first step
	// of replacing that replay with construction. Capture: account-stricken998-character-create-no-worldenter
	// (editor avatar handle 0x021662C8).
	public class CreateEditorRecordTests
	{
		[Fact]
		public void BehaviorControlCenterIir_MatchesCapturedBytes()
		{
			// WorldPrep behavior (handle 0x00039232, slot 7, behaviorId 41413, phase 1) and a CreationScript one
			// (handle 0x0009FF94, slot 5, behaviorId 85843, phase 2).
			Assert.Equal("0000c350000392320004000000070000000a08c5c302180128183000", Convert.ToHexString(CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x00039232, 7, 41413, 1, 24, 0)).ToLowerInvariant());
			Assert.Equal("0000c3500009ff940004000000050000000a08d39e05180228183000", Convert.ToHexString(CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF94, 5, 85843, 2, 24, 0)).ToLowerInvariant());
		}

		[Fact]
		public void BehaviorControlCenterIirLong_MatchesCapturedBytes()
		{
			// The three 116-byte item-bound BehaviorControlCenterIIR records of the WorldPrep stream
			// (handle 0x02000001, slot 9, behaviorId 42011, field5 8): each binds the behavior to one of the
			// burst's equipment pieces (OPC1/M9W9/CUP5 + its handle + 16-byte transform). decode_create_iir_records.py
			// validated all 3 against WorldPrepBurst.bin.
			Assert.Equal(
				"0000c350020000010004000000090000000a089bc8021801280830000000000000000010" + "0004f2500004f25000000000000000014f504331" + "037ac2fe60e046bf84923d28bad0f252037ac2fe60e046bf84923d28bad0f252" +
				"b290805c29e627cab290805c29e627ca000000000000000000000000",
				Convert
					.ToHexString(CreateEditorRecordBuilder.BuildBehaviorControlCenterIirLong(0x02000001,
						9,
						42011,
						1,
						8,
						0,
						0,
						0x10,
						0x0004F250,
						"OPC1",
						[0x03, 0x7A, 0xC2, 0xFE, 0x60, 0xE0, 0x46, 0xBF, 0x84, 0x92, 0x3D, 0x28, 0xBA, 0xD0, 0xF2, 0x52]))
					.ToLowerInvariant());
		}

		[Fact]
		public void CharacterActionIir_MatchesCapturedBytes()
		{
			// First + last CharacterActionIIR of the WorldPrep stream (handle 0x02000001): grants action ids
			// 0x012C and 0x050E24 (decode_character_action.py validated all 26 records).
			Assert.Equal("0000c35002000001000000001d000000000000000000000000000000000000012c0000000000000000", Convert.ToHexString(CreateEditorRecordBuilder.BuildCharacterActionIir(0x02000001, 0x0000012C)).ToLowerInvariant());
			Assert.Equal("0000c35002000001000000001d0000000000000000000000000000000000050e240000000000000000", Convert.ToHexString(CreateEditorRecordBuilder.BuildCharacterActionIir(0x02000001, 0x00050E24)).ToLowerInvariant());
		}

		[Fact]
		public void CharInPlayIir_MatchesCapturedBytes()
		{
			// The "character in play" signal: type=C350 handle=021662C8 flag=00 (the whole message is the handle).
			var bytes = CreateEditorRecordBuilder.BuildCharInPlayIir(0x021662C8);
			Assert.Equal("0000c350021662c800", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void ComboBookIir_MatchesCapturedBytes()
		{
			// First ComboBook of the WorldPrep stream (handle 0x02000001, combo id 0x00416544).
			Assert.Equal("0000c35002000001000100416544", Convert.ToHexString(CreateEditorRecordBuilder.BuildComboBookIir(0x02000001, 0x00416544)).ToLowerInvariant());
		}

		[Fact]
		public void EnergyIir_MatchesCapturedBytes()
		{
			// The editor's single EnergyIIR: type=C350 handle=021662C8 flag=01 value=0.
			var bytes = CreateEditorRecordBuilder.BuildEnergyIir(0x0000C350, 0x021662C8, 0x01, 0);
			Assert.Equal("0000c350021662c80100000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void FeedbackIir_MatchesCapturedBytes()
		{
			// The world-entry FeedbackIIR: type=C350 handle=021662C8 flag=01, payload [0, 0x64, 0x096A3A84][u8 01].
			var bytes = CreateEditorRecordBuilder.BuildFeedbackIir(0x021662C8, 0x01, 0x00000000, 0x00000064, 0x096A3A84, 0x01);
			Assert.Equal("0000c350021662c8010000000000000064096a3a8401", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void GameTimeIir_MatchesCapturedBytes()
		{
			// First GameTimeIIR of the editor stream: dayTime=0x0147929b00000000, flag=02,
			// timestamp=0x000192926a25b9bd, rate=1.0 (3f800000 BE).
			var bytes = CreateEditorRecordBuilder.BuildGameTimeIir(0x0147929B00000000UL, 0x02, 0x000192926A25B9BDUL);
			Assert.Equal("00000000000000000147929b0000000002000192926a25b9bd3f800000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Theory,
		 InlineData(260f,
			 0f,
			 290f,
			 0x00000FA0u,
			 0x00000000u,
			 3u,
			 0x01EA984Eu,
			 15f,
			 13.949005126953125f,
			 0x00000080u,
			 "000000000000000000438200000000000043910000620000c79c00000fa00000000000000000000000000000000300009c5001ea984e0000000000000000180000000101ea984e0000cb2041700000415f2f2000000000ffffffffffffffff0000000700000080"),
		 InlineData(322.5f,
			 145.3113250732422f,
			 704.9000244140625f,
			 0x00000FAAu,
			 0x01316E71u,
			 6u,
			 0x01EA985Cu,
			 15f,
			 12.273988723754883f,
			 0x00000000u,
			 "00000000000000000043a1400043114fb34430399a620000c79c00000faa0000000001316e71000000000000000600009c5001ea985c0000000000000000180000000101ea985c0000cb20417000004144624200000000ffffffffffffffff0000000700000000")]
		// fa0 (Stygian Galley) and faa (Tortage Island) MapChange records from the create captures.
		public void PlayfieldFiir_MatchesCapturedBytes(float x, float y, float z, uint mapId, uint charInstance, uint count, uint token, float nearClip, float farClip, uint flags, string expected)
		{
			var bytes = CreateEditorRecordBuilder.BuildPlayfieldFiir(x, y, z, mapId, charInstance, count, token, nearClip, farClip, flags);
			Assert.Equal(expected, Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void RegenIir_MatchesCapturedBytes()
		{
			// The WorldPrep RegenIIR (handle 0x02000001, flag 0, v1=0x7EE v2=0 v3=0x415) plus a second record from
			// the demol80 capture (v1=0x4F81). RegenIIR_c::ReadSubClass = 3x big-endian u32; decode_create_iir_records.py
			// validated all 590 records across the captures.
			Assert.Equal("0000c3500200000100000007ee0000000000000415", Convert.ToHexString(CreateEditorRecordBuilder.BuildRegenIir(0x02000001, 0x000007EE, 0, 0x00000415)).ToLowerInvariant());
			Assert.Equal("0000c350110099bd0000004f810000000000000000", Convert.ToHexString(CreateEditorRecordBuilder.BuildRegenIir(0x110099BD, 0x00004F81, 0, 0)).ToLowerInvariant());
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
		public void StatIir_FlagOne_MatchesCapturedBytes()
		{
			// A flag=1 record from the same stream: statId 0x000F4285, value 0.
			var bytes = CreateEditorRecordBuilder.BuildStatIir(0x0000C350, 0x021662C8, 0x01, 0x000F4285, 0);
			Assert.Equal("0000c350021662c801000f428500000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void StatIir_MatchesCapturedBytes()
		{
			// First StatIIR of the editor stream: type=C350 handle=021662C8 flag=00 statId=0x000F4266 value=0.
			var bytes = CreateEditorRecordBuilder.BuildStatIir(0x0000C350, 0x021662C8, 0x00, 0x000F4266, 0);
			Assert.Equal("0000c350021662c800000f426600000000", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		[Fact]
		public void StatUpdateIir_MatchesCapturedBytes()
		{
			// First StatUpdate (handle 0x0009FF92, 3 pairs) and the 6-pair record (handle 0x02000001).
			Assert.Equal("0000c3500009ff9200000000030000001b0000189c000001f80000189c000001fa0000189c",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF92, [(0x1B, 0x189C), (0x1F8, 0x189C), (0x1FA, 0x189C)])).ToLowerInvariant());
			Assert.Equal("0000c3500200000100000000060000001b00001f40000000ba00000000000001ff00000000000002000000030100000201000000000000025f59ea5c54",
				Convert.ToHexString(CreateEditorRecordBuilder.BuildStatUpdateIir(0x02000001, [(0x1B, 0x1F40), (0xBA, 0), (0x1FF, 0), (0x200, 0x0301), (0x201, 0), (0x25F, 0x59EA5C54)])).ToLowerInvariant());
		}
	}
}
