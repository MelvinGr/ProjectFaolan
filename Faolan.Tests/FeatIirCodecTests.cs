using System;
using Faolan.Core.Network.Codecs.Feats;
using Xunit;

namespace Faolan.Tests
{
	// Locks the FeatIIR train/ack codec to the captured hakotep train exchange (the body after the 0x2000 length
	// prefix). See Faolan/GameServer/GameServerListener.Feats.cs / docs/subsystems/feats.md.
	public class FeatIirCodecTests
	{
		// The s2c ACK (state 0x05) from Hakotep_hox_login_gameplay...train-feats: handle 0x1101B4F5, feats
		// [110000, 112700, 410600], list marker 0x0FC4 = (3+1)*0x3F1.
		private const string AckHex = "28da68a00000c3501101b4f500050000000000000fc40001adb00001b83c000643e8";
		private const string RequestHex = "28da68a00000c3501101b4f500040000000000000fc40001adb00001b83c000643e8";

		[Fact]
		public void Build_MatchesTheCapturedAck()
		{
			var body = FeatIirCodec.Build(0x1101B4F5, FeatIirCodec.StateAck, [110000u, 112700u, 410600u]);
			Assert.Equal(AckHex, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void Parse_ReadsTheCapturedTrainRequest()
		{
			Assert.True(FeatIirCodec.TryParse(Convert.FromHexString(RequestHex), out var handle, out var state, out var ids));
			Assert.Equal(0x1101B4F5u, handle);
			Assert.Equal(FeatIirCodec.StateTrainRequest, state);
			Assert.Equal(new[] { 110000u, 112700u, 410600u }, ids);
		}

		[Fact]
		public void BuildParse_RoundTrips_IncludingEmpty()
		{
			foreach (var feats in new[] { new uint[] { }, [110004u], [80004u, 82704u, 410604u] })
			{
				var body = FeatIirCodec.Build(0x02000001, FeatIirCodec.StateTrainRequest, feats);
				Assert.True(FeatIirCodec.TryParse(body, out var handle, out var state, out var ids));
				Assert.Equal(0x02000001u, handle);
				Assert.Equal(FeatIirCodec.StateTrainRequest, state);
				Assert.Equal(feats, ids);
			}
		}

		[Fact]
		public void TryParse_RejectsGarbage()
		{
			Assert.False(FeatIirCodec.TryParse([0x01, 0x02, 0x03], out _, out _, out _));
			Assert.False(FeatIirCodec.TryParse(Convert.FromHexString("deadbeef0000c3501101b4f50004000000000000000003f1"), out _, out _, out _));
		}

		// The untrain-all confirm dialog, locked to the captured exchange.
		[Fact]
		public void UntrainMessageBox_Build_MatchesCapture()
		{
			// s2c DispatchFeatUntrainMessageBox: [kind 1][value 0x1A3BDA][0][token 0x48018760].
			var body = FeatUntrainMessageBoxCodec.Build(1, 0x001A3BDA, 0x48018760);
			Assert.Equal("00000001001a3bda0000000048018760", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void UntrainMessageBox_ParseAnswer_ReadsCapture()
		{
			// c2s FeatUntrainAnswer: [answer 0x01][the 16-byte payload echoed]; token = the echo's 4th u32.
			Assert.True(FeatUntrainMessageBoxCodec.TryParseAnswer(Convert.FromHexString("0100000001001a3bda0000000048018760"), out var answer, out var token));
			Assert.Equal(FeatUntrainMessageBoxCodec.AnswerConfirm, answer);
			Assert.Equal(0x48018760u, token);
		}
	}
}
