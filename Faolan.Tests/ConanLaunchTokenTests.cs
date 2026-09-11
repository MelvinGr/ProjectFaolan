using Faolan.Core.Crypt;
using Xunit;

namespace Faolan.Tests
{
	// The ConanPatcher "-key" launch token is TEA-encrypt(time||time) under the patcher's hardcoded key
	// (ConanPatcher.exe: sub_439140 is TEA, sub_4bcf93 is time()). See Faolan.Core.Crypt.ConanLaunchToken.
	public class ClientLaunchTokenTests
	{
		[Fact]
		public void ForUnixTime_ReproducesCapturedLauncherKey()
		{
			// A real ConanPatcher launch passed `-key 495cd3b8c7047b47`; RE shows it = TEA(t||t) with
			// t = 1781038901 (2026-06-09 21:01:41 UTC). The C# (via Tea.EncryptBlock) must reproduce it byte-for-byte.
			Assert.Equal("495cd3b8c7047b47", ClientLaunchToken.ForUnixTime(1781038901));
		}

		[Fact]
		public void ForUnixTime_RoundTrips()
		{
			const uint t = 1700000000;
			Assert.Equal(t, ClientLaunchToken.GetUnixTime(ClientLaunchToken.ForUnixTime(t)));
		}

		[Fact]
		public void GetUnixTime_RecoversTheTimestamp()
		{
			Assert.Equal(1781038901u, ClientLaunchToken.GetUnixTime("495cd3b8c7047b47"));
		}

		// The cases are, in order: empty, too short, right length but not hex, and too long.
		[Theory, InlineData(""), InlineData("tooShort"), InlineData("zzzzzzzzzzzzzzzz"), InlineData("0123456789abcdef00")]
		public void GetUnixTime_RejectsMalformed(string token)
		{
			Assert.Null(ClientLaunchToken.GetUnixTime(token));
		}

		[Fact]
		public void Now_ProducesADecodableFreshToken()
		{
			var token = ClientLaunchToken.Now();
			Assert.Equal(16, token.Length);
			Assert.NotNull(ClientLaunchToken.GetUnixTime(token));
		}
	}
}
