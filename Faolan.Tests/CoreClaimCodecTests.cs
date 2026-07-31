using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact for the CoreClaimIIR (0x06EC1255) "new claims available" glow - the short push that lights the
	// client's Claims/Redeem button (sub 0x0009, no payload). Body = [opcode][0xC350 Character][handle][0x00 0x09].
	// Oracle: the Faolanx necromancer login capture, where an account with unseen claims produced these 14 bytes at
	// world entry (GameServerListener frames it under a u32 length). The full claim list (sub 0x0001) is
	// CoreClaimCodecTests; see docs/subsystems/item-claims.md. Guards the codec output against silent drift.
	public class CoreClaimCodecTests
	{
		private const string CapturedBody = "06ec12550000c350013078740009";

		[Fact]
		public void Build_MatchesCapturedClaim()
		{
			Assert.Equal(CapturedBody, Convert.ToHexString(CoreClaimCodec.Build(new CoreClaimNewClaimsIIR(0x01307874))).ToLowerInvariant());
		}
	}
}
