using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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

		// Sub 0x000A, the payload-less c2s the client sends the moment the claim list lands. Refused before the
		// fix, which made a real claims-window session unreadable. xxx_mage-necromancer_login_play4 frame 11237 - the
		// only capture in the corpus of the window being opened rather than replayed.
		[Fact]
		public void ParseListAck_RoundTripsTheCapturedBody()
		{
			var captured = Convert.FromHexString("06ec12550000c35001317cf8000a");

			var parsed = Assert.IsType<CoreClaimListAckIIR>(CoreClaimCodec.Parse(captured));

			Assert.Equal(0x01317CF8u, parsed.Handle);
			Assert.Equal(CoreClaimSubOpcode.ListAck, parsed.Sub);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(CoreClaimCodec.Build(parsed)));
		}

		// An unmodelled sub must throw: the switch models the subs retail sends and is not tolerant of the rest.
		[Fact]
		public void Parse_StillRejectsAnUnmodelledSub()
		{
			Assert.Throws<CodecParseException>(() => CoreClaimCodec.Parse(Convert.FromHexString("06ec12550000c35001317cf800ff")));
		}
	}
}
