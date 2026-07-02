using System;
using System.Linq;
using System.Text;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// Robustness gate for the MIXD container parse (#293). The client's MIXDTextureLoader reads a chain of
	// sub-textures ([compact-normal][BC5 normal]["ATI1"][BC4 detail] for the body atlases); the BC4 detail is the
	// FINAL sub-texture and occupies a full smallest-first mip chain to the end. The old parse located the "ATI1"
	// split with a blind LastIndexOf, which a coincidental "ATI1" byte-run INSIDE the compressed detail data would
	// hijack (it falls after the real tag). The parse now anchors the tag by its computed position
	// (length - fullBc4Chain - 4) and validates the bytes, so planted "ATI1" noise no longer shifts the result.
	public class FctxMixdContainerTests
	{
		// 8x8, single mip: BC4 detail = 2x2 blocks x8 = 32 B, BC5 normal top mip = 2x2 x16 = 64 B.
		private const int W = 8, H = 8;
		private const int Bc4Chain = 32, Bc5Top = 64;

		private static byte[] Detail()
		{
			var detail = new byte[Bc4Chain];
			for (var i = 0; i < detail.Length; i++) detail[i] = (byte)(0x80 + (i % 53));
			"ATI1"u8.ToArray().CopyTo(detail, 20); // a spurious tag INSIDE the detail data -> what LastIndexOf grabs
			return detail;
		}

		private static byte[] BuildMixd()
		{
			// pixels = [prefix: 24 B compact-normal + 64 B BC5][ "ATI1" ][32 B BC4 detail w/ a planted "ATI1" run]
			var prefix = new byte[24 + Bc5Top];
			for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(0x40 + (i % 37)); // deterministic, no "ATI1" run

			var pixels = prefix.Concat("ATI1"u8.ToArray()).Concat(Detail()).ToArray();

			var header = new byte[24];
			Encoding.ASCII.GetBytes("FCTX").CopyTo(header, 0);
			header[12] = W; header[14] = H; header[16] = 1; // width u16, height u16, mip u8
			Encoding.ASCII.GetBytes("MIXD").CopyTo(header, 20);
			return header.Concat(pixels).ToArray();
		}

		[Fact]
		public void MixdNormal_AnchorsBcChainDespiteSpuriousAti1RunInDetail()
		{
			// The real tag sits at pixels[88]; the planted "ATI1" at pixels[112] is what a blind LastIndexOf would
			// pick, shifting the BC5 window into the detail region. The computed anchor reads the correct 64-byte
			// BC5 top mip ending at the real tag: pixels[24..88] -> DecodeBc5Normal of exactly those bytes.
			Assert.True(FctxConverter.TryDecodeMixdNormal(BuildMixd(), out var got, out var w, out var h));
			Assert.Equal((W, H), (w, h));

			var prefix = new byte[24 + Bc5Top];
			for (var i = 0; i < prefix.Length; i++) prefix[i] = (byte)(0x40 + (i % 37));
			var expected = BcDecoder.DecodeBc5Normal(prefix.AsSpan(24, Bc5Top), W, H);
			Assert.Equal(expected, got);
		}

		[Fact]
		public void MixdDetail_DecodesFullChainDespiteSpuriousAti1RunInDetail()
		{
			// Under the old LastIndexOf split the planted tag (pixels[112]) leaves only 20 detail bytes after it -
			// shorter than the 32-byte top mip - so the decode returned [] (failed). The computed anchor keeps the
			// full 32-byte detail chain and decodes it correctly.
			Assert.True(FctxConverter.TryDecodeTopMip(BuildMixd(), out var got, out _, out _, includeSingleChannel: true));
			Assert.Equal(BcDecoder.DecodeBc4(Detail(), W, H), got);
		}
	}
}
