using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Converts an Age of Conan FCTX texture container (RDB types 1010004/1010006/... ) to a standard DDS file.
	// C# port of rdbreader's fctxconverter.cpp (philippelatulippe/rdbreader, GPLv3). FCTX is a thin wrapper around the
	// same block-compressed (DXT1/3/5) data DDS uses; the only real work is (1) writing a DDS header from the FCTX
	// header and (2) reversing the mipmap order - FCTX stores DXT mip chains smallest-first, DDS expects largest-first.
	//
	// We slice an in-memory payload (RdbReader.ReadDecompressed already unwrapped IOz1/zlib) rather than seeking a file,
	// which makes the mip reorder a straight gather instead of the C++ relative-seek dance.
	public static class FctxConverter
	{
		private const uint DdsMagic = 0x20534444; // "DDS "

		// DDS header flag bits.
		private const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000;
		private const uint DDPF_FOURCC = 0x4;
		private const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;

		public readonly record struct FctxHeader(string FourCc, int Width, int Height, int MipCount, uint Unknown1, uint Unknown2);

		public readonly record struct Result(bool Ok, byte[] Dds, FctxHeader Header, string? Warning)
		{
			public static Result Fail(string warning) => new Result(false, [], default, warning);
		}

		// True if the payload starts with the FCTX magic (after RdbReader has unwrapped any IOz1/zlib container).
		public static bool IsFctx(ReadOnlySpan<byte> payload) =>
			payload.Length >= 4 && payload[0] == (byte)'F' && payload[1] == (byte)'C' && payload[2] == (byte)'T' && payload[3] == (byte)'X';

		public static Result Convert(ReadOnlySpan<byte> payload)
		{
			if (!IsFctx(payload))
				return Result.Fail("not an FCTX payload");
			if (payload.Length < 24)
				return Result.Fail("FCTX payload shorter than its 24-byte header");

			// FCTX header: magic[4], unknown1 u32, unknown2 u32, width u16, height u16, mip1 u8, mip2 u8, unk3 u16, fourcc[4].
			var unknown1 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
			var unknown2 = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
			int width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
			int height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));
			int mip = payload[16];
			var fourCc = Encoding.ASCII.GetString(payload.Slice(20, 4));
			if (mip < 1)
				mip = 1;

			var header = new FctxHeader(fourCc, width, height, mip, unknown1, unknown2);
			var pixels = payload[24..];

			var blockSize = BlockSizeFor(fourCc);
			var ddsFourCc = fourCc == "MIXD" ? "ATI1" : fourCc; // MIXD maps onto DDS ATI1 (single-channel BC4)

			byte[] body;
			string? warning = null;
			if (blockSize > 0)
			{
				body = ReorderMipsLargestFirst(pixels, width, height, mip, blockSize, out warning);
			}
			else
			{
				// Unknown/uncompressed fourcc: copy straight through (matches the C++ fall-through path).
				body = pixels.ToArray();
				warning = $"unrecognized fourcc '{fourCc}' - copied verbatim, mip order not reversed";
			}

			var dds = BuildDds(width, height, mip, ddsFourCc, body);
			return new Result(true, dds, header, warning);
		}

		// Decode the full-resolution (top) mip of an FCTX directly to RGBA32 - for embedding a base-colour PNG in glTF.
		// Decodes the BC1/BC3 (DXT1/3/5) colour formats (the diffuse maps). MIXD (BC4 single-channel normal/detail maps)
		// return false UNLESS includeSingleChannel is set - the diffuse/material selectors rely on that false to skip
		// normal maps; only explicit inspection (the debug texture endpoint) opts in to decode MIXD as grayscale.
		public static bool TryDecodeTopMip(ReadOnlySpan<byte> payload, out byte[] rgba, out int width, out int height,
			bool includeSingleChannel = false)
		{
			rgba = [];
			width = height = 0;
			if (!IsFctx(payload) || payload.Length < 24)
				return false;

			width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
			height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));
			int mip = Math.Max(1, (int)payload[16]);
			var fourCc = Encoding.ASCII.GetString(payload.Slice(20, 4));
			var blockSize = BlockSizeFor(fourCc);
			if (blockSize == 0 || (fourCc == "MIXD" && !includeSingleChannel))
				return false;

			var pixels = payload[24..];
			// Smallest-first layout: the top mip is last; skip every smaller mip (n=mip..2).
			var topOffset = 0;
			for (var n = mip; n >= 2; n--)
				topOffset += MipSize(width, height, n, blockSize);

			var topLen = MipSize(width, height, 1, blockSize);
			if (topOffset + topLen > pixels.Length)
				return false;

			rgba = fourCc == "MIXD"
				? BcDecoder.DecodeBc4(pixels.Slice(topOffset, topLen), width, height)
				: BcDecoder.Decode(pixels.Slice(topOffset, topLen), width, height, fourCc);
			return true;
		}

		// FCTX stores a DXT mip chain smallest-first; gather it back into DDS order (largest mip first).
		private static byte[] ReorderMipsLargestFirst(ReadOnlySpan<byte> pixels, int width, int height, int mip, int blockSize, out string? warning)
		{
			warning = null;
			var chunks = new byte[mip + 1][]; // 1-indexed: chunk[n] = mip level n (n=1 is full size)
			var pos = 0;
			for (var n = mip; n >= 1; n--) // file order = smallest (highest n) first
			{
				var sz = MipSize(width, height, n, blockSize);
				if (pos + sz > pixels.Length)
				{
					sz = Math.Max(0, pixels.Length - pos);
					warning = "FCTX pixel data shorter than the computed mip chain - output truncated";
				}

				chunks[n] = pixels.Slice(pos, sz).ToArray();
				pos += sz;
			}

			using var ms = new MemoryStream();
			for (var n = 1; n <= mip; n++) // DDS order = largest first
				ms.Write(chunks[n]);
			return ms.ToArray();
		}

		// BCn block size at mip level n (1-based; n=1 is full resolution). Blocks cover 4x4 texel tiles.
		private static int MipSize(int width, int height, int n, int blockSize)
		{
			var w = Math.Max(1, width >> (n - 1));
			var h = Math.Max(1, height >> (n - 1));
			var blocksW = Math.Max(1, (w + 3) / 4);
			var blocksH = Math.Max(1, (h + 3) / 4);
			return blocksW * blocksH * blockSize;
		}

		private static int BlockSizeFor(string fourCc) => fourCc switch
		{
			"DXT1" => 8,  // BC1
			"DXT3" => 16, // BC2
			"DXT5" => 16, // BC3
			"MIXD" => 8,  // BC4 (-> ATI1)
			_ => 0
		};

		private static byte[] BuildDds(int width, int height, int mip, string fourCc, byte[] body)
		{
			var dds = new byte[128 + body.Length];

			void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(off, 4), v);

			U32(0, DdsMagic);
			U32(4, 124); // header size
			U32(8, DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_MIPMAPCOUNT);
			U32(12, (uint)height);
			U32(16, (uint)width);
			// pitchOrLinearSize (20), depth (24) left 0; mipMapCount:
			U32(28, (uint)mip);
			// reserved1[11] @32..76 left 0.
			// DDS_PIXELFORMAT @76:
			U32(76, 32); // pixelformat size
			U32(80, DDPF_FOURCC);
			Encoding.ASCII.GetBytes(fourCc).CopyTo(dds.AsSpan(84, 4)); // fourCC
			U32(88, 0x18); // RGBBitCount (vestigial under fourcc, mirrors the C++ guess)
			U32(92, 0x00ff0000); // R mask
			U32(96, 0x0000ff00); // G mask
			U32(100, 0x000000ff); // B mask
			// A mask (104) left 0.
			U32(108, DDSCAPS_COMPLEX | DDSCAPS_MIPMAP | DDSCAPS_TEXTURE); // caps
			// caps2..4 (112,116,120) + reserved2 (124) left 0.

			body.CopyTo(dds.AsSpan(128));
			return dds;
		}
	}
}
