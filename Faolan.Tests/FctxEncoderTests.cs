using System;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// Round-trip gate for the FCTX/DXT1 encoder (#295 Phase 2): a synthetic image encoded to FCTX and read back
	// through the production FctxConverter decoder must reproduce the right dimensions and (within DXT1's lossy
	// tolerance) the right colours. Proves the encoder emits a container + block layout the reader (= the client)
	// accepts — the capability needed to write replaced/upscaled textures back into the RDB.
	public class FctxEncoderTests
	{
		[Theory]
		[InlineData(64, 64)]
		[InlineData(256, 128)] // non-square
		[InlineData(16, 16)]
		public void EncodeDxt1_RoundTripsThroughFctxConverter(int w, int h)
		{
			var src = Gradient(w, h);

			var fctx = FctxEncoder.EncodeDxt1(src, w, h);

			Assert.True(FctxConverter.IsFctx(fctx));
			Assert.True(FctxConverter.TryDecodeTopMip(fctx, out var rgba, out var dw, out var dh));
			Assert.Equal((w, h), (dw, dh));

			// Mean absolute per-channel error must be within DXT1's block-compression tolerance (endpoints are 5/6/5
			// and 4 palette entries per block, so a smooth gradient stays close).
			long err = 0;
			for (var i = 0; i < w * h; i++)
			for (var c = 0; c < 3; c++)
				err += Math.Abs(src[i * 4 + c] - rgba[i * 4 + c]);

			var mae = err / (double)(w * h * 3);
			Assert.True(mae < 12, $"DXT1 round-trip MAE {mae:F2} too high");
		}

		[Fact]
		public void EncodeDxt1_FlatColour_IsExactEnough()
		{
			var w = 32;
			var h = 32;
			var src = new byte[w * h * 4];
			for (var i = 0; i < w * h; i++)
			{
				src[i * 4] = 200;
				src[i * 4 + 1] = 100;
				src[i * 4 + 2] = 50;
				src[i * 4 + 3] = 255;
			}

			var fctx = FctxEncoder.EncodeDxt1(src, w, h);
			Assert.True(FctxConverter.TryDecodeTopMip(fctx, out var rgba, out _, out _));

			// A flat block quantises to a single 5/6/5 endpoint - within ~8 per channel.
			Assert.InRange(rgba[0], 192, 208);
			Assert.InRange(rgba[1], 92, 108);
			Assert.InRange(rgba[2], 42, 58);
		}

		[Fact]
		public void WrapIOz1_RoundTripsThroughReader()
		{
			var fctx = FctxEncoder.EncodeDxt1(Gradient(32, 32), 32, 32);
			var wrapped = FctxEncoder.WrapIOz1(fctx);

			// The reader's IOz1 unwrap must recover the exact FCTX bytes (this is what RdbWriter inlines).
			var unwrapped = RdbReader.UnwrapIOz1Public(wrapped);
			Assert.Equal(fctx, unwrapped);
		}

		private static byte[] Gradient(int w, int h)
		{
			var rgba = new byte[w * h * 4];
			for (var y = 0; y < h; y++)
			for (var x = 0; x < w; x++)
			{
				var i = (y * w + x) * 4;
				rgba[i] = (byte)(x * 255 / Math.Max(1, w - 1));
				rgba[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
				rgba[i + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
				rgba[i + 3] = 255;
			}

			return rgba;
		}
	}
}
