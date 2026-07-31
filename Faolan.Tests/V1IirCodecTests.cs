using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trips for the V1 IIR types whose wire grammar was recovered from the client readers
	// (docs/re/v1-iir-readers.md) - Parse(captured) -> Build must reproduce the capture, and the fields must decode as
	// the reader does. Retires RegenIIR / LockedToChangedIIR / ChainKillIIR from the V1 (no-codec) tier.
	//
	// ⚠️ The captured bodies below are the WHOLE carrier payload, so their leading four bytes are the u32 length -
	// which #530 rule 2 moved off the codecs and onto the sender. `Record(hex)` drops it; the wire is unchanged.
	public class V1IirCodecTests
	{
		[Fact]
		public void AreaFormula_RoundTripsCapturedBody()
		{
			var body = Record("00000031ce30be4e0000c35001316e7100000007e20000c35000017f80003006fe000007e243a5347d431173f7442aea0e00000000");
			var a = AreaFormulaCodec.Parse(body);
			Assert.Equal(0x01316e71u, a.Handle);
			Assert.Single(a.Refs);
			Assert.Equal((0x0000c350u, 0x00017f80u), a.Refs[0]);
			Assert.Equal(0x003006feu, a.FieldA);
			Assert.Single(a.Positions);
			Assert.Equal(body, AreaFormulaCodec.Build(a));
		}

		[Fact]
		public void ChainKill_RoundTripsCapturedBody()
		{
			var body = Record("000000110eed70680000c35001316e710100000002");
			var c = ChainKillCodec.Parse(body);
			Assert.Equal(0x01316e71u, c.Handle);
			Assert.Equal((byte)0x01, c.Env);
			Assert.Equal(2u, c.Count);
			Assert.Equal(body, ChainKillCodec.Build(c));
		}

		// SetShadowCoverageIIR (0xD6C1F0CC) - the last V1 IIR type. Both captured pairs are the same exchange: the
		// CLIENT reports its shadow coverage (env 0x01) and the server echoes it back (env 0x00). Two characters on
		// two maps, both carrying 100.
		[Theory]
		[InlineData("00000011d6c1f0cc0000c3501101b4f50100000064", 0x1101b4f5u, 0x01)] // Hakotep, c2s
		[InlineData("00000011d6c1f0cc0000c3501101b4f50000000064", 0x1101b4f5u, 0x00)] // …and the server's echo
		[InlineData("00000011d6c1f0cc0000c3500802e5d40100000064", 0x0802e5d4u, 0x01)] // NecroL80, c2s
		[InlineData("00000011d6c1f0cc0000c3500802e5d40000000064", 0x0802e5d4u, 0x00)] // …and the server's echo
		public void SetShadowCoverage_RoundTripsCapturedBody(string hex, uint handle, byte env)
		{
			var body = Record(hex);
			var c = SetShadowCoverageCodec.Parse(body);

			Assert.Equal(handle, c.Handle);
			Assert.Equal(env, c.Env);
			Assert.Equal(100u, c.Percent);
			Assert.Equal(body, SetShadowCoverageCodec.Build(c));
		}

		// The direction lives in the env byte, and Build defaults to the SERVER's - sending the client's value back at
		// it would be reporting the client's own reading as if it were ours.
		[Fact]
		public void SetShadowCoverage_BuildDefaultsToTheServerEnv()
		{
			Assert.Equal(Record("00000011d6c1f0cc0000c3500802e5d40000000064"), SetShadowCoverageCodec.Build(0x0802e5d4, 100));
			Assert.Equal(Record("00000011d6c1f0cc0000c3500802e5d40100000064"),
				SetShadowCoverageCodec.Build(0x0802e5d4, 100, SetShadowCoverageCodec.ClientEnv));
		}

		[Fact]
		public void Crime_RoundTripsCapturedBody()
		{
			var body = Record("00000012c79545040000c35008006f31000100000001");
			var c = CrimeCodec.Parse(body);
			Assert.Equal(0x08006f31u, c.Handle);
			Assert.Equal((byte)0x01, c.Flag);
			Assert.Equal(1u, c.Value);
			Assert.Equal(body, CrimeCodec.Build(c));
		}

		[Fact]
		public void Feedback_RoundTripsCapturedBody()
		{
			var body = Record("0000001aba1fd2220000c35001316e71010000000000000064096a3a8401");
			var f = FeedbackCodec.Parse(body);
			Assert.Equal(0x01316e71u, f.Handle);
			Assert.Equal((byte)0x01, f.Env);
			Assert.Equal(100u, f.Value);
			Assert.Equal(0x096a3a84u, f.Source);
			Assert.Equal((byte)0x01, f.Flag);
			Assert.Equal(body, FeedbackCodec.Build(f));
		}

		[Fact]
		public void KnubotFinishTrade_RoundTripsCapturedBody()
		{
			var body = Record("000000237446f3fa0000c35001316e710000010000c35000004fec000000000000000000000000");
			var k = KnubotFinishTradeCodec.Parse(body);
			Assert.Equal(0x01316e71u, k.Handle);
			Assert.Equal((ushort)1, k.Version);
			Assert.Equal(0x0000c350u, k.TargetClass);
			Assert.Equal(0x00004fecu, k.TargetInstance);
			Assert.Equal(body, KnubotFinishTradeCodec.Build(k));
		}

		[Fact]
		public void KnubotStartTrade_FramesAndCarriesTheDialoguePayloadVerbatim()
		{
			// [len=0x1f][subop 4b2a47bf][c350][handle][env][18-byte payload] - the Knubot XML shop payload rides verbatim.
			var body = Record("0000001f4b2a47bf0000c35001316e710000010000c35000004fec00000000deadbeef");
			var k = KnubotStartTradeCodec.Parse(body);
			Assert.Equal(0x01316e71u, k.Handle);
			Assert.Equal(Convert.FromHexString("00010000c35000004fec00000000deadbeef"), k.Payload);
			Assert.Equal(body, KnubotStartTradeCodec.Build(k));
		}

		[Fact]
		public void LockedToChanged_RoundTripsCapturedBody()
		{
			var body = Record("000000156b0aec580000c3500004deea000000c35002165eff");
			var l = LockedToChangedCodec.Parse(body);
			Assert.Equal(0x0004deeau, l.Handle);
			Assert.Equal(0x0000c350u, l.TargetClass); // Character
			Assert.Equal(0x02165effu, l.TargetInstance);
			Assert.Equal(body, LockedToChangedCodec.Build(l));
		}

		[Fact]
		public void Regen_RoundTripsCapturedBody()
		{
			var body = Record("0000001928d0ac990000c35002165eff00000000000000000000000415");
			var r = RegenCodec.Parse(body);
			Assert.Equal(0x02165effu, r.Handle);
			Assert.Equal(0, r.HealthDelta);
			Assert.Equal(0, r.ManaDelta);
			Assert.Equal(1045, r.StaminaDelta); // ×100 wire scale = +10.45 stamina (a sprint-stamina refill tick)
			Assert.Equal(body, RegenCodec.Build(r));
		}

		[Fact]
		public void SpellBook_RoundTripsCapturedBody()
		{
			var body = Record("000000116ba109360000c35001316e7100000003f1");
			var sb = SpellBookCodec.Parse(body);
			Assert.Equal(0x01316e71u, sb.Handle);
			Assert.Equal(0, sb.Count); // 0x3F1 IS the empty list, not a marker
			Assert.Equal(body, SpellBookCodec.Build(sb));
		}

		[Fact]
		public void TemplateAction_RoundTripsCapturedBody()
		{
			var body = Record("000000696c3009370000c35001316df100004a1f2c004a1f2c00000000000000145455363145e485bb5235153d50b6c704d40ad68845e485bb5235153d50b6c704d40ad688b290805c29e627cab290805c29e627ca000000010000000300000000000000000000000000000000");
			var t = TemplateActionCodec.Parse(body);
			Assert.Equal(0x01316df1u, t.Handle);
			Assert.Equal(TemplateActionCodec.TemplateBytes, t.Template.Length);
			Assert.Equal(1u, t.F1);
			Assert.Equal(3u, t.F2);
			Assert.Equal(body, TemplateActionCodec.Build(t));
		}

		[Fact]
		public void UpdateInventory_RoundTripsCapturedBody_AndCarriesTheItemProtobuf()
		{
			var body = Record("00000019a74d28140000c35000017e3600000000080a06086510b6fc05");
			var u = UpdateInventoryCodec.Parse(body);
			Assert.Equal(0x00017e36u, u.Handle);
			// the length-prefixed protobuf (a ProtoInventoryGame) is carried intact.
			Assert.Equal(Convert.FromHexString("0a06086510b6fc05"), u.InventoryProtobuf);
			Assert.Equal(body, UpdateInventoryCodec.Build(u));
		}

		// The captured payload WITHOUT its leading u32 length - the sender's, since #530 rule 2.
		private static byte[] Record(string hex)
		{
			return Convert.FromHexString(hex)[4..];
		}

	}
}
