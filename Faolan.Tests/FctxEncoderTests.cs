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

		[Theory]
		[InlineData(64, 64)]
		[InlineData(128, 32)]
		public void EncodeDxt5_RoundTripsColourAndAlpha(int w, int h)
		{
			var src = GradientWithAlpha(w, h);

			var fctx = FctxEncoder.Encode(src, w, h, "DXT5");

			// includeSingleChannel:false path decodes DXT5 as colour; verify dims + colour, then check alpha survives.
			Assert.True(FctxConverter.TryDecodeTopMip(fctx, out var rgba, out var dw, out var dh));
			Assert.Equal((w, h), (dw, dh));

			long colErr = 0, aErr = 0;
			for (var i = 0; i < w * h; i++)
			{
				for (var c = 0; c < 3; c++)
					colErr += Math.Abs(src[i * 4 + c] - rgba[i * 4 + c]);
				aErr += Math.Abs(src[i * 4 + 3] - rgba[i * 4 + 3]);
			}

			Assert.True(colErr / (double)(w * h * 3) < 12, "DXT5 colour MAE too high");
			Assert.True(aErr / (double)(w * h) < 12, "DXT5 alpha MAE too high");
		}

		[Fact]
		public void EncodeBc5_RoundTripsNormalXY()
		{
			int w = 64, h = 64;
			var normal = NormalMap(w, h);

			var bc5 = BcEncoder.EncodeBc5(normal, w, h);
			var back = BcDecoder.DecodeBc5Normal(bc5, w, h);

			long xErr = 0, yErr = 0;
			for (var i = 0; i < w * h; i++)
			{
				xErr += Math.Abs(normal[i * 4] - back[i * 4]);
				yErr += Math.Abs(normal[i * 4 + 1] - back[i * 4 + 1]);
			}

			Assert.True(xErr / (double)(w * h) < 10, "BC5 X MAE too high");
			Assert.True(yErr / (double)(w * h) < 10, "BC5 Y MAE too high");
		}

		[Fact]
		public void EncodeMixd_RoundTripsNormalAndDetail()
		{
			int w = 64, h = 64;
			var normal = NormalMap(w, h);
			var detail = new byte[w * h];
			for (var y = 0; y < h; y++)
			for (var x = 0; x < w; x++)
				detail[y * w + x] = (byte)((x ^ y) & 0xff);

			var mixd = FctxEncoder.EncodeMixd(normal, detail, w, h);
			Assert.True(FctxConverter.IsFctx(mixd));

			// Normal: TryDecodeMixdNormal reads the top BC5 mip (before the ATI1 tag).
			Assert.True(FctxConverter.TryDecodeMixdNormal(mixd, out var nrm, out var nw, out var nh));
			Assert.Equal((w, h), (nw, nh));
			long nErr = 0;
			for (var i = 0; i < w * h; i++)
				nErr += Math.Abs(normal[i * 4] - nrm[i * 4]) + Math.Abs(normal[i * 4 + 1] - nrm[i * 4 + 1]);
			Assert.True(nErr / (double)(w * h * 2) < 10, "MIXD normal MAE too high");

			// Detail: TryDecodeTopMip(includeSingleChannel) routes MIXD to the ATI1/BC4 detail.
			Assert.True(FctxConverter.TryDecodeTopMip(mixd, out var det, out var dw, out var dh, includeSingleChannel: true));
			Assert.Equal((w, h), (dw, dh));
			long dErr = 0;
			for (var i = 0; i < w * h; i++)
				dErr += Math.Abs(detail[i] - det[i * 4]); // detail decoded as grayscale (R=value)
			Assert.True(dErr / (double)(w * h) < 12, "MIXD detail MAE too high");
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

		private static byte[] NormalMap(int w, int h)
		{
			// A smooth tangent-space normal field (R=X, G=Y around 128 = flat, gently varying).
			var rgba = new byte[w * h * 4];
			for (var y = 0; y < h; y++)
			for (var x = 0; x < w; x++)
			{
				var i = (y * w + x) * 4;
				rgba[i] = (byte)(128 + 60 * Math.Sin(x * 0.2));     // X
				rgba[i + 1] = (byte)(128 + 60 * Math.Sin(y * 0.2)); // Y
				rgba[i + 2] = 255;                                  // Z (reconstructed on decode)
				rgba[i + 3] = 255;
			}

			return rgba;
		}

		private static byte[] GradientWithAlpha(int w, int h)
		{
			var rgba = Gradient(w, h);
			for (var y = 0; y < h; y++)
			for (var x = 0; x < w; x++)
				rgba[(y * w + x) * 4 + 3] = (byte)(x * 255 / Math.Max(1, w - 1)); // horizontal alpha ramp
			return rgba;
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
