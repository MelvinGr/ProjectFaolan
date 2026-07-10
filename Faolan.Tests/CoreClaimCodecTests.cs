using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact for CoreClaimIIR (0x06EC1255) - the post-ready-burst "core claim" that opens the world session.
	// Body = [subop][0xC350 Character][handle][0x00 0x09]. Oracle: the Faolanx necromancer login capture, where the
	// claim for handle 0x01307874 produced these 14 bytes (GameServerListener.SendPostReadyBurst frames it under a
	// u32 length). Guards the codec output so a refactor can't silently drift it.
	public class CoreClaimCodecTests
	{
		private const string CapturedBody = "06ec12550000c350013078740009";

		[Fact]
		public void Build_MatchesCapturedClaim()
		{
			Assert.Equal(CapturedBody, Convert.ToHexString(CoreClaimCodec.Build(0x01307874)).ToLowerInvariant());
		}
	}
}
