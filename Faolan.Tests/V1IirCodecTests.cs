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
		public void AreaFormula_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("00000031ce30be4e0000c35001316e7100000007e20000c35000017f80003006fe000007e243a5347d431173f7442aea0e00000000");
			var a = AreaFormulaCodec.Parse(body);
			Assert.Equal(0x01316e71u, a.Handle);
			Assert.Single(a.Refs);
			Assert.Equal((0x0000c350u, 0x00017f80u), a.Refs[0]);
			Assert.Equal(0x003006feu, a.FieldA);
			Assert.Single(a.Positions);
			Assert.Equal(body, a.ToArray());
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
		public void Crime_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("00000012c79545040000c35008006f31000100000001");
			var c = CrimeCodec.Parse(body);
			Assert.Equal(0x08006f31u, c.Handle);
			Assert.Equal((byte)0x01, c.Flag);
			Assert.Equal(1u, c.Value);
			Assert.Equal(body, c.ToArray());
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
		public void KnubotFinishTrade_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("000000237446f3fa0000c35001316e710000010000c35000004fec000000000000000000000000");
			var k = KnubotFinishTradeCodec.Parse(body);
			Assert.Equal(0x01316e71u, k.Handle);
			Assert.Equal((ushort)1, k.Version);
			Assert.Equal(0x0000c350u, k.TargetClass);
			Assert.Equal(0x00004fecu, k.TargetInstance);
			Assert.Equal(body, k.ToArray());
		}

		[Fact]
		public void KnubotStartTrade_FramesAndCarriesTheDialoguePayloadVerbatim()
		{
			// [len=0x1f][subop 4b2a47bf][c350][handle][env][18-byte payload] - the Knubot XML shop payload rides verbatim.
			var body = Convert.FromHexString("0000001f4b2a47bf0000c35001316e710000010000c35000004fec00000000deadbeef");
			var k = KnubotStartTradeCodec.Parse(body);
			Assert.Equal(0x01316e71u, k.Handle);
			Assert.Equal(Convert.FromHexString("00010000c35000004fec00000000deadbeef"), k.Payload);
			Assert.Equal(body, k.ToArray());
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
		public void SpellBook_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString("000000116ba109360000c35001316e7100000003f1");
			var sb = SpellBookCodec.Parse(body);
			Assert.Equal(0x01316e71u, sb.Handle);
			Assert.Equal(0x3F1u, sb.ListMarker);
			Assert.Equal(body, sb.ToArray());
		}

		[Fact]
		public void TemplateAction_RoundTripsCapturedBody()
		{
			var body = Convert.FromHexString(
				"000000696c3009370000c35001316df100004a1f2c004a1f2c00000000000000145455363145e485bb5235153d50b6c704d40ad68845e485bb5235153d50b6c704d40ad688b290805c29e627cab290805c29e627ca000000010000000300000000000000000000000000000000");
			var t = TemplateActionCodec.Parse(body);
			Assert.Equal(0x01316df1u, t.Handle);
			Assert.Equal(TemplateActionCodec.TemplateBytes, t.Template.Length);
			Assert.Equal(1u, t.F1);
			Assert.Equal(3u, t.F2);
			Assert.Equal(body, t.ToArray());
		}

		[Fact]
		public void UpdateInventory_RoundTripsCapturedBody_AndCarriesTheItemProtobuf()
		{
			var body = Convert.FromHexString("00000019a74d28140000c35000017e3600000000080a06086510b6fc05");
			var u = UpdateInventoryCodec.Parse(body);
			Assert.Equal(0x00017e36u, u.Handle);
			// the length-prefixed protobuf (a ProtoInventoryGame) is carried intact.
			Assert.Equal(Convert.FromHexString("0a06086510b6fc05"), u.InventoryProtobuf);
			Assert.Equal(body, u.ToArray());
		}
	}
}
