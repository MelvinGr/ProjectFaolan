using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Faolan.Core.Network.Packets
{
	// The recipe-book list carried in TradeskillReplyIIR_c (0x9062D559), the server's answer to the client's
	// TradeskillRequestIIR_c (0x06B9CD94) when the Crafting -> Recipes window opens. Grammar recovered from
	// AgeOfConan.exe (client readers FUN_007aab30 -> FUN_007abb30 -> per-record FUN_0077be70; the 4-byte reader is
	// FUN_0048f900, big-endian) and validated byte-exact against NecroL80_..._open-crrafting-receipies frame 2926
	// (603 recipes, 209309 bytes consumed exactly). See docs/tradeskill-recipes.md.
	//
	// Reply IIR body (after the [subop][0xC350][handle] carrier prefix):
	//   [u8 env=0][u32 version=1][u8 type][u32 c]  then, when type==3, c is 0x3F1-framed (c=(recipes+1)*0x3F1):
	//   recipes x record. type 1 = a single RecipeLearnt (not modelled here); type 3 = the full book list.
	// Each record:
	//   [u32 listKey]                                  # the recipe id (== the map key)
	//   [u32 x3]                                        # header (@c = 0xf4241 = tradeskill RDB type)
	//   0x3F1-list of 18-u32 elements                   # ingredients (item id + item DecryptionKeys)
	//   [u32 x4]
	//   0x3F1-list of u32                               # id list
	//   0x3F1-list of 17-u32 elements                   # results
	//   [u32]                                           # @50
	//   [u8]                                            # trailing flag (@0x5c)
	public static class TradeskillRecipeCodec
	{
		public const uint ListFrame = 0x3F1;
		public const byte ListType = 3; // full recipe-book list (vs 1 = single RecipeLearnt)
		public const uint Version = 1;

		// A single parsed recipe: its id (the record's list-key) and the verbatim record body AFTER that id.
		public readonly record struct RecipeRecord(uint Id, byte[] Data);

		// Parse the reply IIR body (starting at the [u8 env] byte, i.e. just past [subop][0xC350][handle]) into its
		// recipe records. Throws on a malformed stream so a bad decode surfaces rather than silently truncating.
		public static List<RecipeRecord> ParseReplyBody(ReadOnlySpan<byte> body)
		{
			var p = 0;
			var env = body[p]; p += 1;
			var version = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4)); p += 4;
			if (version != Version)
				throw new InvalidOperationException($"TradeskillReply: version {version} != {Version}");
			var type = body[p]; p += 1;
			if (type != ListType)
				throw new InvalidOperationException($"TradeskillReply: type {type} != {ListType} (single-recipe form not supported)");
			_ = env;

			var count = ReadFramed(body, ref p);
			var recipes = new List<RecipeRecord>(count);
			for (var i = 0; i < count; i++)
			{
				var start = p;
				var id = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4));
				var len = RecordLength(body, start);
				recipes.Add(new RecipeRecord(id, body.Slice(start + 4, len - 4).ToArray()));
				p = start + len;
			}

			return recipes;
		}

		// Build the reply IIR body [env][version][type=3][count][records] from stored recipe rows.
		public static byte[] BuildReplyBody(IReadOnlyCollection<RecipeRecord> recipes)
		{
			ArgumentNullException.ThrowIfNull(recipes);
			var body = new List<byte>(recipes.Count * 340 + 16)
			{
				0x00 // env
			};
			AddU32(body, Version);
			body.Add(ListType);
			AddU32(body, ((uint)recipes.Count + 1) * ListFrame);
			foreach (var r in recipes)
			{
				AddU32(body, r.Id);
				body.AddRange(r.Data);
			}

			return body.ToArray();
		}

		// The byte length of one record starting at `start` (walking the fixed fields + the three 0x3F1-framed
		// sub-lists + the trailing flag byte). Ground truth for segmenting the verbatim record blobs.
		public static int RecordLength(ReadOnlySpan<byte> body, int start)
		{
			var p = start;
			p += 4; // list-key
			p += 12; // header x3
			SkipList(body, ref p, 18); // ingredients: 18-u32 elements
			p += 16; // mid x4
			SkipList(body, ref p, 1); // id list: u32 elements
			SkipList(body, ref p, 17); // results: 17-u32 elements
			p += 4; // @50
			p += 1; // trailing flag
			return p - start;
		}

		private static int ReadFramed(ReadOnlySpan<byte> body, ref int p)
		{
			var c = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4));
			p += 4;
			if (c == 0 || c % ListFrame != 0)
				throw new InvalidOperationException($"TradeskillReply: list count 0x{c:X} is not a positive multiple of 0x{ListFrame:X}");
			return (int)(c / ListFrame) - 1;
		}

		private static void SkipList(ReadOnlySpan<byte> body, ref int p, int elementU32)
		{
			var n = ReadFramed(body, ref p);
			p += n * elementU32 * 4;
		}

		private static void AddU32(List<byte> to, uint value)
		{
			to.Add((byte)(value >> 24));
			to.Add((byte)(value >> 16));
			to.Add((byte)(value >> 8));
			to.Add((byte)value);
		}
	}
}
