using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Feats;
using Xunit;

namespace Faolan.Tests
{
	// Locks the FeatIIR train/ack codec to the captured hakotep train exchange (the body after the 0x2000 length
	// prefix). See Faolan/GameServer/Feats/FeatService.cs / docs/subsystems/feats.md.
	public class FeatCodecTests
	{
		// The s2c ack (state 0x05) from Hakotep_hox_login_gameplay...train-feats: handle 0x1101B4F5, feats
		// [110000, 112700, 410600], list marker 0x0FC4 = (3+1)*0x3F1.
		private const string AckHex = "28da68a00000c3501101b4f500050000000000000fc40001adb00001b83c000643e8";
		private const string RequestHex = "28da68a00000c3501101b4f500040000000000000fc40001adb00001b83c000643e8";

		[Fact]
		public void BuildParse_RoundTrips_IncludingEmpty()
		{
			foreach (var feats in new[] { new uint[] { }, [110004u], [80004u, 82704u, 410604u] })
			{
				var body = FeatCodec.Build(0x02000001, FeatCodec.StateTrainRequest, feats);
				var parsed = FeatCodec.Parse(body);

				Assert.Equal(0x02000001u, parsed.CharHandle);
				Assert.Equal(FeatCodec.StateTrainRequest, parsed.State);
				Assert.Equal(feats, parsed.FeatIds);
			}
		}

		[Fact]
		public void Build_MatchesTheCapturedAck()
		{
			var body = FeatCodec.Build(0x1101B4F5, FeatCodec.StateAck, [110000u, 112700u, 410600u]);
			Assert.Equal(AckHex, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void Parse_ReadsTheCapturedTrainRequest()
		{
			var parsed = FeatCodec.Parse(Convert.FromHexString(RequestHex));

			Assert.Equal(0x1101B4F5u, parsed.CharHandle);
			Assert.Equal(FeatCodec.StateTrainRequest, parsed.State);
			Assert.Equal(new[] { 110000u, 112700u, 410600u }, parsed.FeatIds);
		}

		// Rule 4: Parse throws. A short body, a wrong sub-opcode word and a list that promises more ids than the body
		// carries are all decode failures naming the offset - never a `false` the caller can forget to check.
		[Fact]
		public void Parse_RejectsGarbage()
		{
			Assert.Throws<CodecParseException>(() => FeatCodec.Parse([0x01, 0x02, 0x03]));
			Assert.Throws<CodecParseException>(() => FeatCodec.Parse(Convert.FromHexString("deadbeef0000c3501101b4f50004000000000000000003f1")));
			Assert.Throws<CodecParseException>(() => FeatCodec.Parse(Convert.FromHexString("28da68a00000c3501101b4f500040000000000000fc4")));
		}

		// A trailing byte the grammar cannot account for is a failure too - that is what EnsureAtEnd() is for, and it
		// is the check a fixed-offset reader structurally cannot make.
		[Fact]
		public void Parse_RejectsATrailingByte()
		{
			Assert.Throws<CodecParseException>(() => FeatCodec.Parse(Convert.FromHexString(AckHex + "00")));
		}

		// The untrain-all confirm dialog, locked to the captured exchange.
		[Fact]
		public void UntrainMessageBox_Build_MatchesCapture()
		{
			// s2c DispatchFeatUntrainMessageBox: [kind 1][value 0x1A3BDA][0][token 0x48018760].
			var body = FeatUntrainMessageBoxCodec.Build(1, 0x001A3BDA, 0x48018760);
			Assert.Equal("00000001001a3bda0000000048018760", Convert.ToHexString(body).ToLowerInvariant());
			Assert.Equal(new FeatUntrainMessageBox(1, 0x001A3BDA, 0x48018760), FeatUntrainMessageBoxCodec.Parse(body));
		}

		[Fact]
		public void UntrainAnswer_Parse_ReadsCapture()
		{
			// c2s FeatUntrainAnswer: [answer 0x01][the 16-byte message box echoed]; the token is the echo's 4th u32.
			var captured = Convert.FromHexString("0100000001001a3bda0000000048018760");
			var reply = FeatUntrainAnswerCodec.Parse(captured);

			Assert.Equal(FeatUntrainAnswerCodec.AnswerConfirm, reply.Answer);
			Assert.Equal(0x48018760u, reply.Echo.Token);
			Assert.Equal(captured, FeatUntrainAnswerCodec.Build(reply));
		}
	}
}
