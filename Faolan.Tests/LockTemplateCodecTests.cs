using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// `DispatchLockTemplate` is the spell cooldown - what greys an action-bar button and runs its sweep. There
	// is no recast GameAction on the wire at all (0 across all 132 dumps), so this message is the whole mechanism,
	// and we sent none of the 992 the corpus carries.
	//
	// The bytes below are retail's, verbatim (`Hakotep_hox_login_gameplay0`, frame 76). Every captured body is
	// 8 bytes; of the 741 whose spell id resolves against `Spells`, 580 carry exactly `RecastCentiseconds / 100`.
	public class LockTemplateCodecTests
	{
		// One captured body: `Contract of Protection` rank 1, locked for its own 2000 cs recast = 20.00 s.
		private static readonly byte[] Captured = Convert.FromHexString("0002FA6541A00000");

		private const uint CapturedSpell = 195173;
		private const float CapturedSeconds = 20.0f;

		[Fact]
		public void Parse_ReadsTheCapturedBody()
		{
			var value = LockTemplateCodec.Parse(Captured);

			Assert.Equal(CapturedSpell, value.TemplateId);
			Assert.Equal(CapturedSeconds, value.SecondsRemaining);
		}

		[Fact]
		public void Build_RoundTripsTheCapturedBody_ByteExact()
		{
			Assert.Equal(Convert.ToHexString(Captured), Convert.ToHexString(LockTemplateCodec.Build(LockTemplateCodec.Parse(Captured))));
			Assert.Equal(Convert.ToHexString(Captured), Convert.ToHexString(LockTemplateCodec.Build(CapturedSpell, CapturedSeconds)));
		}

		// The float is big-endian, like every other raw-body float. Little-endian would put the exponent in the last
		// byte and send 20.0 as 0000A041 - a denormal the client would read as ~0, i.e. no cooldown at all.
		[Fact]
		public void TheSecondsField_IsBigEndian()
		{
			var body = LockTemplateCodec.Build(CapturedSpell, CapturedSeconds);

			Assert.Equal(0x41, body[4]);
			Assert.Equal(0xA0, body[5]);
		}

		// A partial lock - retail re-sends the time left rather than the base (15.143 s against the same spell's
		// 20.00 s recast), the same restore shape a long buff's duration takes.
		[Fact]
		public void APartialLock_RoundTrips()
		{
			var partial = Convert.FromHexString("0002FA65417249BA");
			var value = LockTemplateCodec.Parse(partial);

			Assert.Equal(CapturedSpell, value.TemplateId);
			Assert.InRange(value.SecondsRemaining, 15.14f, 15.15f);
			Assert.Equal(Convert.ToHexString(partial), Convert.ToHexString(LockTemplateCodec.Build(value)));
		}

		[Fact]
		public void Parse_ThrowsOnAShortOrOverlongBody()
		{
			Assert.ThrowsAny<Exception>(() => LockTemplateCodec.Parse(new byte[7]));
			Assert.ThrowsAny<Exception>(() => LockTemplateCodec.Parse(new byte[9]));
		}
	}
}
