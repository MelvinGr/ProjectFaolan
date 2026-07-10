using System;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trips for the V1 IIR types whose wire grammar was recovered from the client readers
	// (docs/re/v1-iir-readers.md) - Parse(captured) -> ToArray() must reproduce the capture, and the fields must
	// decode as the reader does. Retires RegenIIR / LockedToChangedIIR / ChainKillIIR from the V1 (no-codec) tier.
	public class V1IirCodecTests
	{
		[Fact]
		public void Regen_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("0000001928d0ac990000c35002165eff00000000000000000000000415");
			var r = RegenCodec.Parse(body);
			Assert.Equal(0x02165effu, r.Handle);
			Assert.Equal(0u, r.FieldA);
			Assert.Equal(0u, r.FieldB);
			Assert.Equal(1045u, r.Value);
			Assert.Equal(body, r.ToArray());
		}

		[Fact]
		public void LockedToChanged_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("000000156b0aec580000c3500004deea000000c35002165eff");
			var l = LockedToChangedCodec.Parse(body);
			Assert.Equal(0x0004deeau, l.Handle);
			Assert.Equal(0x0000c350u, l.TargetClass); // Character
			Assert.Equal(0x02165effu, l.TargetInstance);
			Assert.Equal(body, l.ToArray());
		}

		[Fact]
		public void ChainKill_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("000000110eed70680000c35001316e710100000002");
			var c = ChainKillCodec.Parse(body);
			Assert.Equal(0x01316e71u, c.Handle);
			Assert.Equal((byte)0x01, c.Env);
			Assert.Equal(2u, c.Count);
			Assert.Equal(body, c.ToArray());
		}

		[Fact]
		public void SpellBook_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("000000116ba109360000c35001316e7100000003f1");
			var sb = SpellBookCodec.Parse(body);
			Assert.Equal(0x01316e71u, sb.Handle);
			Assert.Equal(0x3F1u, sb.ListMarker);
			Assert.Equal(body, sb.ToArray());
		}

		[Fact]
		public void Feedback_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("0000001aba1fd2220000c35001316e71010000000000000064096a3a8401");
			var f = FeedbackIirCodec.Parse(body);
			Assert.Equal(0x01316e71u, f.Handle);
			Assert.Equal((byte)0x01, f.Env);
			Assert.Equal(100u, f.Value);
			Assert.Equal(0x096a3a84u, f.Source);
			Assert.Equal((byte)0x01, f.Flag);
			Assert.Equal(body, f.ToArray());
		}

		[Fact]
		public void Crime_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("00000012c79545040000c35008006f31000100000001");
			var c = CrimeCodec.Parse(body);
			Assert.Equal(0x08006f31u, c.Handle);
			Assert.Equal((byte)0x01, c.Flag);
			Assert.Equal(1u, c.Value);
			Assert.Equal(body, c.ToArray());
		}
	}
}
