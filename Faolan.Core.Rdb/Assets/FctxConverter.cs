using System;
using System.IO;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Converts an Age of Conan FCTX texture container (RDB types 1010004, 1010006 and friends) to a standard DDS
	///     file. Derived from rdbreader's fctxconverter.cpp (philippelatulippe/rdbreader, GPLv3).
	/// </summary>
	/// <remarks>
	///     FCTX is a thin wrapper around the same block-compressed (DXT1/3/5) data DDS uses, so the only real work is
	///     writing a DDS header from the FCTX header and reversing the mipmap order: FCTX stores DXT mip chains
	///     smallest-first, DDS expects largest-first. The whole payload is sliced in memory —
	///     <see cref="RdbReader.ReadDecompressed" /> has already unwrapped IOz1/zlib — which makes the mip reorder a
	///     straight gather.
	/// </remarks>
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

			// FCTX header, 24 bytes: magic[4], version u32, mapsMask u32, width u16, height u16, numMips u8,
			// numMipsInFile u8, pad u16, fourcc[4]. The field names are the client loader's own:
			//
			//   * FUN_00de0f00 reads the magic ("FCTX" = 0x58544346 LE) and switches on the next u32 - 1 selects the
			//     legacy loader FUN_00de08c0, 2 the current one FUN_00de0bd0, anything else is unsupported. So the
			//     word at +4 is the container version: 2 on 2,801 textures, 1 on the 7 legacy ones whose fourcc slot
			//     holds no readable tag.
			//   * The v2 loader popcounts bits 0x1|0x2|0x4 of the word at +8 and reads that many map chains, so it is
			//     a bitmask of which maps the container carries, not a layout id and not a plain count. 1 (bit 0) is
			//     the base image alone, which every single-image format uses; MIXD's 6 (bits 1+2) is two additional
			//     maps and its rare 2 (bit 1) is one, matching the measured 1.36x payload-per-pixel split between
			//     them.
			//   * Bytes +16/+17 are m_nNumMips and m_nNumMipsInFile - the loader asserts them equal ("The m_nNumMips
			//     field, and the m_nNumMipsInFile should be the same") - and the u16 at +18 is read and discarded.
			var version = payload.ReadU32Le(4);
			var mapsMask = payload.ReadU32Le(8);
			int width = payload.ReadU16Le(12);
			int height = payload.ReadU16Le(14);
			int mip = payload[16];
			var fourCc = Encoding.ASCII.GetString(payload.Slice(20, 4));
			if (mip < 1)
				mip = 1;

			var header = new FctxHeader(fourCc, width, height, mip, version, mapsMask);
			var pixels = payload[24..];

			// MIXD is a container of several concatenated maps (see DecodeMixdDetail); the usable single-channel body
			// detail map is the trailing ATI1/BC4 sub-chain. It must be narrowed to before reordering, or the DDS is
			// built from the wrong bytes. Its "ATI1" tag doubles as the DDS fourcc, so the export is a standard
			// single-channel DDS.
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
				// Unknown or uncompressed fourcc: copy straight through.
				body = pixels.ToArray();
				warning = $"unrecognized fourcc '{fourCc}' - copied verbatim, mip order not reversed";
			}

			var dds = BuildDds(width, height, mip, ddsFourCc, body);
			return new Result(true, dds, header, warning);
		}

		/// <summary>
		///     Whether the payload starts with the FCTX magic, once <see cref="RdbReader" /> has unwrapped any
		///     IOz1/zlib container.
		/// </summary>
		public static bool IsFctx(ReadOnlySpan<byte> payload)
		{
			return payload.Length >= 4 && payload[0] == (byte)'F' && payload[1] == (byte)'C' && payload[2] == (byte)'T' && payload[3] == (byte)'X';
		}

		/// <summary>
		///     Decodes the full-resolution tangent-space normal map from a MIXD container to RGBA (X = R, Y = G, Z
		///     reconstructed).
		/// </summary>
		/// <returns>False when the payload carries no MIXD/ATI1 chain, or the chain is too short.</returns>
		/// <remarks>
		///     The MIXD body-atlas layout (1024x1024) is [6 B/block compact-normal][BC5/ATI2 normal chain][ "ATI1" ]
		///     [BC4 detail chain]. The compact-normal seg1 is the client's DXT5-fallback source (loader FUN_00ddf960),
		///     redundant with the BC5 chain rather than a gloss or AO map, so there is nothing to extract from it. The
		///     BC5 normal chain sits immediately before the "ATI1" tag and is smallest-first, so its full-resolution
		///     mip is the topLen bytes ending at the tag.
		/// </remarks>
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

		/// <summary>
		///     Decodes the full-resolution (top) mip of an FCTX directly to RGBA32, which is how a base-colour PNG gets
		///     embedded in glTF. The BC1/BC3 (DXT1/3/5) colour formats — the diffuse maps — are handled.
		/// </summary>
		/// <param name="includeSingleChannel">
		///     Decode a MIXD (BC4 single-channel normal/detail) payload as grayscale instead of refusing it. The
		///     diffuse and material selectors rely on the refusal to skip normal maps, so only explicit inspection,
		///     such as the debug texture endpoint, opts in.
		/// </param>
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

			// MIXD is a container of several concatenated maps, not a single mip chain. The body's surface-detail map
			// is a trailing ATI1 (single-channel BC4) sub-texture carrying its own smallest-first mip chain, so the
			// decode has to anchor on that sub-chain's "ATI1" tag; treating the whole payload as one chain from the
			// start lands mid-container and decodes to noise.
			if (fourCc == "MIXD")
			{
				rgba = DecodeMixdDetail(pixels, width, height, mip);
				return rgba.Length > 0;
			}

			// Smallest-first layout: the top mip is last, so every smaller mip (n = mip..2) is skipped.
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

		/// <summary>
		///     Extracts and decodes the full-resolution mip of the trailing ATI1/BC4 detail map from a MIXD container's
		///     pixel bytes, which are the payload after the 24-byte FCTX header. The sub-chain is smallest-first, so
		///     its full-resolution mip is the last block run.
		/// </summary>
		/// <returns>
		///     An empty array when there is no ATI1 sub-texture, or the chain is too short to hold a full-resolution
		///     mip.
		/// </returns>
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

		/// <summary>
		///     The total bytes of a full smallest-first BCn mip chain (levels 1..mip) at the given block size.
		/// </summary>
		private static int FullChainSize(int width, int height, int mip, int blockSize)
		{
			var total = 0;
			for (var n = 1; n <= mip; n++)
				total += MipSize(width, height, n, blockSize);
			return total;
		}

		/// <summary>
		///     Locates the ATI1 detail sub-texture's tag inside a MIXD container's pixel bytes, which are the payload
		///     after the 24-byte FCTX header.
		/// </summary>
		/// <returns>The offset of the tag, or -1 when there is no ATI1 sub-texture.</returns>
		/// <remarks>
		///     The container is a chain of sub-textures (client MIXDTextureLoader, FUN_00de0bd0: a flags byte gives one
		///     to three sub-textures, each read with its own fourcc). For the body atlases that is [6 B/block
		///     compact-normal][BC5/ATI2 normal][ "ATI1" ][BC4 detail], with the ATI1 detail last. Because the detail
		///     occupies a full smallest-first BC4 mip chain to the end of the payload, its tag sits at exactly
		///     (length - fullBc4Chain - 4). Anchoring on that computed position and validating the four tag bytes
		///     there is robust against a coincidental "ATI1" byte run inside the compressed data, which a blind
		///     LastIndexOf would wrongly pick when it falls after the real tag; the byte scan remains the fallback for
		///     a MIXD whose layout differs.
		/// </remarks>
		private static int LocateAti1Tag(ReadOnlySpan<byte> pixels, int width, int height, int mip)
		{
			var computed = pixels.Length - FullChainSize(width, height, mip, 8) - Ati1Tag.Length;
			if (computed >= 0 && pixels.Slice(computed, Ati1Tag.Length).SequenceEqual(Ati1Tag))
				return computed;

			return pixels.LastIndexOf(Ati1Tag);
		}

		/// <summary>
		///     The byte size of BCn mip level n (1-based, where n = 1 is full resolution). Blocks cover 4x4 texel
		///     tiles.
		/// </summary>
		private static int MipSize(int width, int height, int n, int blockSize)
		{
			var w = Math.Max(1, width >> (n - 1));
			var h = Math.Max(1, height >> (n - 1));
			var blocksW = Math.Max(1, (w + 3) / 4);
			var blocksH = Math.Max(1, (h + 3) / 4);
			return blocksW * blocksH * blockSize;
		}

		/// <summary>
		///     Gathers an FCTX smallest-first DXT mip chain back into DDS order, largest mip first.
		/// </summary>
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

		/// <summary>
		///     The decoded FCTX header. Version and MapsMask carry the client's own semantics, detailed in
		///     <see cref="Convert" />: the loader dispatches on Version (1 legacy, 2 current) and popcounts MapsMask's
		///     bits 0x1|0x2|0x4 for the number of map chains to read.
		/// </summary>
		public readonly record struct FctxHeader(string FourCc, int Width, int Height, int MipCount, uint Version, uint MapsMask);

		public readonly record struct Result(bool Ok, byte[] Dds, FctxHeader Header, string? Warning)
		{
			public static Result Fail(string warning)
			{
				return new Result(false, [], default, warning);
			}
		}
	}
}
