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
	}
}
