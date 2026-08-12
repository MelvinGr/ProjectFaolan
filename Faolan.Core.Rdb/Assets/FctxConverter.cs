using System;
using System.IO;
using System.Text;
using Faolan.Core.Extensions;

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
		private const uint DDPF_FOURCC = 0x4;
		private const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;

		// DDS header flag bits.
		private const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000;
		private const uint DdsMagic = 0x20534444; // "DDS "

		// The ATI1 (BC4) detail sub-texture inside a MIXD container is tagged "ATI1" immediately before its mip data.
		private static ReadOnlySpan<byte> Ati1Tag => "ATI1"u8;

		public static Result Convert(ReadOnlySpan<byte> payload)
		{
			if (!IsFctx(payload))
				return Result.Fail("not an FCTX payload");
			if (payload.Length < 24)
				return Result.Fail("FCTX payload shorter than its 24-byte header");

			// FCTX header: magic[4], unknown1 u32, unknown2 u32, width u16, height u16, mip1 u8, mip2 u8, unk3 u16, fourcc[4].
			//
			// ⭐ The two words are CENSUSED, not unknown-shaped (#464, `--report fctx-header` over 2,808 textures):
			// neither is per-texture data and neither predicts any size, dimension or mip count.
			//
			//   unknown1  2 on every fourcc-tagged texture (DXT1 2,475 / DXT5 115 / MIXD 218), 1 on the 7 whose
			//             fourcc slot holds no readable tag - so it separates "carries a fourcc" from "does not".
			//   unknown2  1 on every single-image format, and 6 on MIXD - the one CONTAINER format, which this
			//             converter already has to special-case as several concatenated maps.
			//
			// ⭐ RESOLVED at a larger sample (#464, 4,397 textures): the MIXD outlier is NINE textures, not one, and
			// unknown2 is NOT a sub-map COUNT - the reading the smaller sample suggested. The two groups separate by
			// PAYLOAD PER PIXEL with no overlap at all, and the bands are 1.36x apart where a 6-vs-2 count would have
			// to be 3x:
			//
			//   MIXD u2=6   x706   payload/px 2.500-2.576
			//   MIXD u2=2   x9     payload/px 1.834-1.848
			//
			// ⚠️ It is a real CONTENT split rather than a size artefact: the two groups OVERLAP on dimensions and mip
			// count (both carry 512x512, both carry mips 6/8/9), so nothing about the image size explains it. So
			// unknown2 selects between two MIXD channel LAYOUTS - which sub-textures the container holds - and the
			// number itself is a layout id, not a count. Naming the two layouts needs the client's MIXDTextureLoader.
			//
			// ⚠️ The superseded reading, recorded because the smaller sample genuinely supported it and someone will
			// re-derive it otherwise: "a value that varies WITHIN a format is a count, not a label, so unknown2 is
			// the number of sub-maps". It is the right INSTINCT and the wrong answer, and only a payload measurement
			// separates the two - a distribution never could.
			var unknown1 = payload.ReadU32Le(4);
			var unknown2 = payload.ReadU32Le(8);
			int width = payload.ReadU16Le(12);
			int height = payload.ReadU16Le(14);
			int mip = payload[16];
			var fourCc = Encoding.ASCII.GetString(payload.Slice(20, 4));
			if (mip < 1)
				mip = 1;

			var header = new FctxHeader(fourCc, width, height, mip, unknown1, unknown2);
			var pixels = payload[24..];

			// MIXD is a CONTAINER of several concatenated maps (see DecodeMixdDetail); the usable single-channel body
			// detail map is the trailing ATI1/BC4 sub-chain. Narrow to it before reordering, else the DDS is built from
			// the wrong bytes. (Its "ATI1" tag doubles as the DDS fourcc, so the export is a standard single-channel DDS.)
			if (fourCc == "MIXD")
			{
				var tag = LocateAti1Tag(pixels, width, height, mip);
				if (tag >= 0)
					pixels = pixels[(tag + Ati1Tag.Length)..];
			}

			var blockSize = BlockSizeFor(fourCc);
			var ddsFourCc = fourCc == "MIXD"
				? "ATI1"
				: fourCc; // MIXD maps onto DDS ATI1 (single-channel BC4)

			byte[] body;
			string? warning = null;
			if (blockSize > 0)
				body = ReorderMipsLargestFirst(pixels, width, height, mip, blockSize, out warning);
			else
			{
				// Unknown/uncompressed fourcc: copy straight through (matches the C++ fall-through path).
				body = pixels.ToArray();
				warning = $"unrecognized fourcc '{fourCc}' - copied verbatim, mip order not reversed";
			}

			var dds = BuildDds(width, height, mip, ddsFourCc, body);
			return new Result(true, dds, header, warning);
		}

		// True if the payload starts with the FCTX magic (after RdbReader has unwrapped any IOz1/zlib container).
		public static bool IsFctx(ReadOnlySpan<byte> payload)
		{
			return payload.Length >= 4 && payload[0] == (byte)'F' && payload[1] == (byte)'C' && payload[2] == (byte)'T' && payload[3] == (byte)'X';
		}

		// Decode the full-res tangent-space NORMAL map from a MIXD container to RGBA (X=R, Y=G, Z reconstructed). The
		// MIXD body-atlas layout (1024x1024) is: [6 B/block compact-normal][BC5/ATI2 normal chain][ "ATI1" ][BC4
		// detail chain] - the compact-normal seg1 is the client's DXT5-fallback source (redundant with the BC5, RE'd
		// via FUN_00ddf960; not a gloss/AO map, so nothing to extract from it). The BC5 normal chain sits immediately
		// before the "ATI1" tag and is smallest-first, so its full-res mip is the topLen bytes ending at the tag
		// (located robustly via LocateAti1Tag). Returns false when there's no MIXD/ATI1 or the chain is too short.
		public static bool TryDecodeMixdNormal(ReadOnlySpan<byte> payload, out byte[] rgba, out int width, out int height)
		{
			rgba = [];
			width = height = 0;
			if (!IsFctx(payload) || payload.Length < 24)
				return false;

			width = payload.ReadU16Le(12);
			height = payload.ReadU16Le(14);
			var mip = Math.Max(1, (int)payload[16]);
			if (Encoding.ASCII.GetString(payload.Slice(20, 4)) != "MIXD")
				return false;

			var pixels = payload[24..];
			var tag = LocateAti1Tag(pixels, width, height, mip);
			var topLen = MipSize(width, height, 1, 16); // BC5 = 16 bytes/block
			if (tag < topLen)
				return false;

			rgba = BcDecoder.DecodeBc5Normal(pixels.Slice(tag - topLen, topLen), width, height);
			return true;
		}

		// Decode the full-resolution (top) mip of an FCTX directly to RGBA32 - for embedding a base-colour PNG in glTF.
		// Decodes the BC1/BC3 (DXT1/3/5) colour formats (the diffuse maps). MIXD (BC4 single-channel normal/detail maps)
		// return false UNLESS includeSingleChannel is set - the diffuse/material selectors rely on that false to skip
		// normal maps; only explicit inspection (the debug texture endpoint) opts in to decode MIXD as grayscale.
		public static bool TryDecodeTopMip(ReadOnlySpan<byte> payload, out byte[] rgba, out int width, out int height, bool includeSingleChannel = false)
		{
			rgba = [];
			width = height = 0;
			if (!IsFctx(payload) || payload.Length < 24)
				return false;

			width = payload.ReadU16Le(12);
			height = payload.ReadU16Le(14);
			var mip = Math.Max(1, (int)payload[16]);
			var fourCc = Encoding.ASCII.GetString(payload.Slice(20, 4));
			var blockSize = BlockSizeFor(fourCc);
			if (blockSize == 0 || (fourCc == "MIXD" && !includeSingleChannel))
				return false;

			var pixels = payload[24..];

			// MIXD ("MIXeD") is NOT a single mip chain - it is a CONTAINER of several concatenated maps. The body's
			// surface-detail map is a trailing ATI1 (single-channel BC4) sub-texture carrying its OWN smallest-first mip
			// chain; decode that sub-chain's full-res mip (anchored on its "ATI1" tag) rather than treating the whole
			// payload as one chain from the start - the latter lands mid-container and decodes to noise.
			if (fourCc == "MIXD")
			{
				rgba = DecodeMixdDetail(pixels, width, height, mip);
				return rgba.Length > 0;
			}

			// Smallest-first layout: the top mip is last; skip every smaller mip (n=mip..2).
			var topOffset = 0;
			for (var n = mip; n >= 2; n--)
				topOffset += MipSize(width, height, n, blockSize);

			var topLen = MipSize(width, height, 1, blockSize);
			if (topOffset + topLen > pixels.Length)
				return false;

			rgba = BcDecoder.Decode(pixels.Slice(topOffset, topLen), width, height, fourCc);
			return true;
		}

		private static int BlockSizeFor(string fourCc)
		{
			return fourCc switch
			{
				"DXT1" => 8, // BC1
				"DXT3" => 16, // BC2
				"DXT5" => 16, // BC3
				"MIXD" => 8, // BC4 (-> ATI1)
				_      => 0
			};
		}

		private static byte[] BuildDds(int width, int height, int mip, string fourCc, byte[] body)
		{
			var dds = new byte[128 + body.Length];

			dds.WriteU32Le(0, DdsMagic);
			dds.WriteU32Le(4, 124); // header size
			dds.WriteU32Le(8, DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_MIPMAPCOUNT);
			dds.WriteU32Le(12, (uint)height);
			dds.WriteU32Le(16, (uint)width);
			// pitchOrLinearSize (20), depth (24) left 0; mipMapCount:
			dds.WriteU32Le(28, (uint)mip);
			// reserved1[11] @32..76 left 0.
			// DDS_PIXELFORMAT @76:
			dds.WriteU32Le(76, 32); // pixelformat size
			dds.WriteU32Le(80, DDPF_FOURCC);
			Encoding.ASCII.GetBytes(fourCc).CopyTo(dds.AsSpan(84, 4)); // fourCC
			dds.WriteU32Le(88, 0x18); // RGBBitCount (vestigial under fourcc, mirrors the C++ guess)
			dds.WriteU32Le(92, 0x00ff0000); // R mask
			dds.WriteU32Le(96, 0x0000ff00); // G mask
			dds.WriteU32Le(100, 0x000000ff); // B mask
			// A mask (104) left 0.
			dds.WriteU32Le(108, DDSCAPS_COMPLEX | DDSCAPS_MIPMAP | DDSCAPS_TEXTURE); // caps
			// caps2..4 (112,116,120) + reserved2 (124) left 0.

			body.CopyTo(dds.AsSpan(128));
			return dds;
		}

		// Extract + decode the full-res mip of the trailing ATI1/BC4 detail map from a MIXD container's pixel bytes (the
		// payload after the 24-byte FCTX header). The sub-chain is smallest-first, so its full-res mip is the last block
		// run. Empty array when no ATI1 sub-texture is present or the chain is too short to hold a full-res mip.
		private static byte[] DecodeMixdDetail(ReadOnlySpan<byte> pixels, int width, int height, int mip)
		{
			var topLen = MipSize(width, height, 1, 8);
			var tag = LocateAti1Tag(pixels, width, height, mip);
			var chain = tag >= 0
				? pixels[(tag + Ati1Tag.Length)..]
				: pixels;
			return chain.Length < topLen
				? []
				: BcDecoder.DecodeBc4(chain.Slice(chain.Length - topLen, topLen), width, height);
		}

		// Total bytes of a full smallest-first BCn mip chain (levels 1..mip) at the given block size.
		private static int FullChainSize(int width, int height, int mip, int blockSize)
		{
			var total = 0;
			for (var n = 1; n <= mip; n++)
				total += MipSize(width, height, n, blockSize);
			return total;
		}

		// Robustly locate the ATI1 detail sub-texture's tag inside a MIXD container's pixel bytes (the payload after
		// the 24-byte FCTX header). The container is a chain of sub-textures (client MIXDTextureLoader,
		// FUN_00de0bd0: a flags byte gives 1-3 sub-textures, each read with its own fourcc); for the body atlases
		// that is [6 B/block compact-normal][BC5/ATI2 normal][ "ATI1" ][BC4 detail], with the ATI1 detail the FINAL
		// sub-texture. Because the detail occupies a full smallest-first BC4 mip chain to the end of the payload, its
		// tag sits at exactly (length - fullBc4Chain - 4). Anchoring by that computed position and VALIDATING the
		// four tag bytes there is robust against a coincidental "ATI1" byte-run inside the compressed data (which a
		// blind LastIndexOf would wrongly pick when it falls after the real tag). Falls back to the byte scan when
		// the computed position doesn't validate (a MIXD whose layout differs), so it is never worse than the scan.
		// Returns the offset of the tag, or -1 if no ATI1 sub-texture is found.
		private static int LocateAti1Tag(ReadOnlySpan<byte> pixels, int width, int height, int mip)
		{
			var computed = pixels.Length - FullChainSize(width, height, mip, 8) - Ati1Tag.Length;
			if (computed >= 0 && pixels.Slice(computed, Ati1Tag.Length).SequenceEqual(Ati1Tag))
				return computed;

			return pixels.LastIndexOf(Ati1Tag);
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

		// `Unknown1`/`Unknown2` keep their names deliberately: the census above says what they CORRELATE with and
		// not what they mean, and a name like `SubMapCount` would be a claim the measurement does not support.
		public readonly record struct FctxHeader(string FourCc, int Width, int Height, int MipCount, uint Unknown1, uint Unknown2);

		public readonly record struct Result(bool Ok, byte[] Dds, FctxHeader Header, string? Warning)
		{
			public static Result Fail(string warning)
			{
				return new Result(false, [], default, warning);
			}
		}
	}
}
