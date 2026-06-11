using Faolan.Core.Crypt;
using Xunit;

namespace Faolan.Tests
{
	// The ConanPatcher "-key" launch token = TEA-encrypt(time||time) with the patcher's hardcoded key, recovered
	// by RE of ConanPatcher.exe (sub_439140 = TEA, sub_4bcf93 = time()). See Faolan.Core.Crypt.ConanLaunchToken.
	public class ConanLaunchTokenTests
	{
		[Fact]
		public void ForUnixTime_ReproducesCapturedLauncherKey()
		{
			// A real ConanPatcher launch passed `-key 495cd3b8c7047b47`; RE shows it = TEA(t||t) with
			// t = 1781038901 (2026-06-09 21:01:41 UTC). The C# (via Tea.EncryptBlock) must reproduce it byte-for-byte.
			Assert.Equal("495cd3b8c7047b47", ConanLaunchToken.ForUnixTime(1781038901));
		}

		[Fact]
		public void ForUnixTime_RoundTrips()
		{
			const uint t = 1700000000;
			Assert.Equal(t, ConanLaunchToken.GetUnixTime(ConanLaunchToken.ForUnixTime(t)));
		}

		[Fact]
		public void GetUnixTime_RecoversTheTimestamp()
		{
			Assert.Equal(1781038901u, ConanLaunchToken.GetUnixTime("495cd3b8c7047b47"));
		}

		[Theory, InlineData(""), InlineData("tooShort"), InlineData("zzzzzzzzzzzzzzzz"), InlineData("0123456789abcdef00")]
		// right length, not hex
		// wrong length
		public void GetUnixTime_RejectsMalformed(string token)
		{
			Assert.Null(ConanLaunchToken.GetUnixTime(token));
		}

		[Fact]
		public void Now_ProducesADecodableFreshToken()
		{
			var token = ConanLaunchToken.Now();
			Assert.Equal(16, token.Length);
			Assert.NotNull(ConanLaunchToken.GetUnixTime(token));
		}
	}
}
