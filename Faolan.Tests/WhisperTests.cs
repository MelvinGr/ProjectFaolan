using System;
using Faolan.AgentServer;
using Xunit;

namespace Faolan.Tests
{
	// Golden tests for the whisper/tell packet (0x001E), byte-exact against the capture
	// account-faolan-character-hoxxling-login_move-from-night-to-day_send-and-receive-whisper
	// (2026-06-12, Hoxxling <-> Settuc 0x01313438). The same wire shape carries both directions:
	// [u16 0x001E][u16 bodyLen][u32 peerCharId][u16 len][text][00][01][00].
	public class WhisperTests
	{
		[Fact]
		public void IncomingWhisper_MatchesCapturedDownload() // Settuc -> Hoxxling: "hello"
		{
			var expected = Convert.FromHexString("001e000e01313438000568656c6c6f000100");

			Assert.Equal(expected, AgentServerListener.BuildWhisper(0x01313438, "hello"));
		}

		[Fact]
		public void OutgoingWhisperShape_MatchesCapturedUpload() // Hoxxling -> Settuc: "HI!" (same grammar)
		{
			var expected = Convert.FromHexString("001e000c01313438000348492100" + "0100");

			Assert.Equal(expected, AgentServerListener.BuildWhisper(0x01313438, "HI!"));
		}
	}
}
