using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Writes an AoC FCTX texture container (#295 Phase 2) — the inverse of FctxConverter's read path. Header
	// (24 B): "FCTX", u32 ver=2, u32 unk=1, u16 width, u16 height, u8 mipCount ×2, u16 0, fourcc; then the DXT
	// mip chain SMALLEST-first (the order the client reads). Currently emits DXT1 (opaque diffuse); DXT5/MIXD are
	// follow-ups. The decompressed RDB entry for a texture IS the FCTX bytes (no separate RDB-Id header), so the
	// output of Encode is ready to be IOz1+zlib-wrapped and inlined via RdbWriter.
	public static class FctxEncoder
	{
		// Encode RGBA32 as a full-mip FCTX blob in `fourCc` (DXT1 = opaque colour, DXT5 = colour + alpha). Mip
		// levels: full res down to 1×1, each dimension = max(1, dim >> level), box-downsampled from the previous.
		public static byte[] Encode(byte[] rgba, int width, int height, string fourCc)
		{
			if (fourCc != "DXT1" && fourCc != "DXT5")
				throw new NotSupportedException($"FCTX encode fourcc '{fourCc}' not supported (DXT1/DXT5 only)");

			var mipCount = MipCount(width, height);
			using var ms = new MemoryStream();

			// Header.
			ms.Write(Encoding.ASCII.GetBytes("FCTX"));
			WriteU32(ms, 2); // Version - the client's loader dispatch (FUN_00de0f00: 1 legacy / 2 current, #464)
			WriteU32(ms, 1); // MapsMask - bit 0 = the base image alone (the single-image encoding this writes)
			WriteU16(ms, (ushort)width);
			WriteU16(ms, (ushort)height);
			ms.WriteByte((byte)mipCount);
			ms.WriteByte((byte)mipCount);
			WriteU16(ms, 0);
			ms.Write(Encoding.ASCII.GetBytes(fourCc));

			// Build every mip's RGBA (level 0 = full res), then write blocks SMALLEST-first.
			var levels = new byte[mipCount][];
			var dims = new (int W, int H)[mipCount];
			levels[0] = rgba;
			dims[0] = (width, height);
			for (var n = 1; n < mipCount; n++)
			{
				var pw = dims[n - 1].W;
				var ph = dims[n - 1].H;
				var nw = Math.Max(1, width >> n);
				var nh = Math.Max(1, height >> n);
				levels[n] = BoxDownsample(levels[n - 1], pw, ph, nw, nh);
				dims[n] = (nw, nh);
			}

			for (var n = mipCount - 1; n >= 0; n--)
			{
				ms.Write(fourCc == "DXT5"
					? BcEncoder.EncodeBc3(levels[n], dims[n].W, dims[n].H)
					: BcEncoder.EncodeBc1(levels[n], dims[n].W, dims[n].H));
			}

			return ms.ToArray();
		}

		// Encode RGBA32 (row-major, top-down) as a full-mip FCTX DXT1 blob.
		public static byte[] EncodeDxt1(byte[] rgba, int width, int height)
		{
			return Encode(rgba, width, height, "DXT1");
		}

		// Encode a MIXD container (#295 / #293): the body normal/detail texture. Layout mirrors what the client
		// reads (loader FUN_00ddf960): FCTX header (fourcc "MIXD", flags=6 = 2 sub-textures) then, per mip
		// smallest-first, [seg1: blocks×6][BC5 normal: blocks×16]; then the "ATI1" tag; then per mip [BC4 detail:
		// blocks×8]. `normalRgba` = the tangent-space normal (R=X,G=Y); `detail` = the single-channel detail map.
		//
		// seg1 is the compact-normal SOURCE the client only reads on the DXT5-fallback path (GPUs without BC5); the
		// native BC5/ATI2 path (all modern GPUs, incl. the Proton/Wine target) SEEKS past it by blocks×6 and never
		// reads its content. So seg1 is emitted as the correct SIZE of zero bytes - valid for the BC5 path. (A real
		// seg1 would only matter for pre-DX10 hardware.) Round-trips through TryDecodeMixdNormal + DecodeMixdDetail.
		public static byte[] EncodeMixd(byte[] normalRgba, byte[] detail, int width, int height)
		{
			var mipCount = MipCount(width, height);
			using var ms = new MemoryStream();

			ms.Write(Encoding.ASCII.GetBytes("FCTX"));
			WriteU32(ms, 2);
			WriteU32(ms, 6); // flags: 2 sub-textures (normal + ATI1 detail), matches real MIXD entries
			WriteU16(ms, (ushort)width);
			WriteU16(ms, (ushort)height);
			ms.WriteByte((byte)mipCount);
			ms.WriteByte((byte)mipCount);
			WriteU16(ms, 0);
			ms.Write(Encoding.ASCII.GetBytes("MIXD"));

			var norm = BuildMipsRgba(normalRgba, width, height, mipCount);
			var det = BuildMipsChannel(detail, width, height, mipCount);
			var dims = MipDims(width, height, mipCount);

			// Normal sub-texture: per mip smallest-first, [seg1 blocks×6 zeros][BC5 blocks×16].
			for (var n = mipCount - 1; n >= 0; n--)
			{
				var blocks = BlockCount(dims[n].W, dims[n].H);
				ms.Write(new byte[blocks * 6]); // seg1 placeholder (skipped by the BC5 path)
				ms.Write(BcEncoder.EncodeBc5(norm[n], dims[n].W, dims[n].H));
			}

			ms.Write(Encoding.ASCII.GetBytes("ATI1"));

			// Detail sub-texture: per mip smallest-first, BC4 blocks×8.
			for (var n = mipCount - 1; n >= 0; n--)
				ms.Write(BcEncoder.EncodeBc4(det[n], dims[n].W, dims[n].H));

			return ms.ToArray();
		}

		// IOz1+zlib-wrap FCTX bytes into the raw on-disk blob a local RDB entry stores (what RdbWriter inlines).
		public static byte[] WrapIOz1(byte[] content)
		{
			using var ms = new MemoryStream();
			ms.Write(Encoding.ASCII.GetBytes("IOz1"));
			WriteU32(ms, (uint)content.Length);
			using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, true))
				zlib.Write(content, 0, content.Length);
			return ms.ToArray();
		}

		private static int BlockCount(int w, int h)
		{
			return Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
		}

		// Simple 2×2 box downsample (clamped for odd dimensions). Good enough for mip generation; the top mip is
		// what's usually sampled and is authored at full quality by the caller (the upscaled image).
		private static byte[] BoxDownsample(byte[] src, int sw, int sh, int dw, int dh)
		{
			var dst = new byte[dw * dh * 4];
			for (var y = 0; y < dh; y++)
			for (var x = 0; x < dw; x++)
			{
				var x0 = Math.Min(x * 2, sw - 1);
				var x1 = Math.Min(x * 2 + 1, sw - 1);
				var y0 = Math.Min(y * 2, sh - 1);
				var y1 = Math.Min(y * 2 + 1, sh - 1);
				for (var c = 0; c < 4; c++)
				{
					var sum = src[(y0 * sw + x0) * 4 + c] + src[(y0 * sw + x1) * 4 + c] + src[(y1 * sw + x0) * 4 + c] + src[(y1 * sw + x1) * 4 + c];
					dst[(y * dw + x) * 4 + c] = (byte)(sum / 4);
				}
			}

			return dst;
		}

		private static byte[] BoxDownsampleChannel(byte[] src, int sw, int sh, int dw, int dh)
		{
			var dst = new byte[dw * dh];
			for (var y = 0; y < dh; y++)
			for (var x = 0; x < dw; x++)
			{
				var x0 = Math.Min(x * 2, sw - 1);
				var x1 = Math.Min(x * 2 + 1, sw - 1);
				var y0 = Math.Min(y * 2, sh - 1);
				var y1 = Math.Min(y * 2 + 1, sh - 1);
				dst[y * dw + x] = (byte)((src[y0 * sw + x0] + src[y0 * sw + x1] + src[y1 * sw + x0] + src[y1 * sw + x1]) / 4);
			}

			return dst;
		}

		private static byte[][] BuildMipsChannel(byte[] channel, int w, int h, int mipCount)
		{
			var levels = new byte[mipCount][];
			levels[0] = channel;
			var (pw, ph) = (w, h);
			for (var n = 1; n < mipCount; n++)
			{
				var nw = Math.Max(1, w >> n);
				var nh = Math.Max(1, h >> n);
				levels[n] = BoxDownsampleChannel(levels[n - 1], pw, ph, nw, nh);
				(pw, ph) = (nw, nh);
			}

			return levels;
		}

		private static byte[][] BuildMipsRgba(byte[] rgba, int w, int h, int mipCount)
		{
			var levels = new byte[mipCount][];
			levels[0] = rgba;
			var (pw, ph) = (w, h);
			for (var n = 1; n < mipCount; n++)
			{
				var nw = Math.Max(1, w >> n);
				var nh = Math.Max(1, h >> n);
				levels[n] = BoxDownsample(levels[n - 1], pw, ph, nw, nh);
				(pw, ph) = (nw, nh);
			}

			return levels;
		}

		private static int MipCount(int w, int h)
		{
			var n = 1;
			while (w >> n > 0 || h >> n > 0)
				n++;
			return n;
		}

		private static (int W, int H)[] MipDims(int w, int h, int mipCount)
		{
			var dims = new (int, int)[mipCount];
			for (var n = 0; n < mipCount; n++)
				dims[n] = (Math.Max(1, w >> n), Math.Max(1, h >> n));
			return dims;
		}

		private static void WriteU16(Stream s, ushort v)
		{
			Span<byte> b = stackalloc byte[2];
			BinaryPrimitives.WriteUInt16LittleEndian(b, v);
			s.Write(b);
		}

		private static void WriteU32(Stream s, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(b, v);
			s.Write(b);
		}
	}
}
