using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The recipe-book list carried in TradeskillReplyIIR_c (0x9062D559), the server's answer to the client's
	// TradeskillRequestIIR_c (0x06B9CD94) when the Crafting -> Recipes window opens. Grammar recovered from
	// AgeOfConan.exe (client readers FUN_007aab30 -> FUN_007abb30 -> per-record FUN_0077be70; the 4-byte reader is
	// FUN_0048f900, big-endian) and validated byte-exact against NecroL80_..._open-crrafting-receipies frame 2926
	// (603 recipes, 209309 bytes consumed exactly). See docs/subsystems/tradeskill-recipes.md.
	//
	// The record is fully DECODED into typed fields (RecipeRecord) - no verbatim blob. Every wire word is either a
	// per-recipe column (RecipeRecord), a per-element field carried by the item (RecipeItem + its Items row's TEA key
	// + hash), or one of the fixed constants below. ParseReplyBody (capture -> records) and BuildReplyBody
	// (records -> wire) round-trip byte-for-byte (TradeskillRecipeCodecTests), so the record CAN be rebuilt entirely
	// from the DB columns (ToRecord) - which is how the live server answers the request.
	//
	// Reply IIR body (after the [subop][0xC350][handle] carrier prefix):
	//   [u8 env=0][u32 version=1][u8 type][u32 c]  then, when type==3, c is 0x3F1-framed (c=(recipes+1)*0x3F1):
	//   recipes x record. type 1 = a single RecipeLearnt (not modelled here); type 3 = the full book list.
	// Each record:
	//   [u32 listKey]                                  # the recipe id (== the map key, and == header word 0)
	//   [u32 id=listKey][u32 header1][u32 rdbType]      # header (rdbType = 0xf4241/0xf4242, the tradeskill RDB type)
	//   0x3F1-list of 18-u32 elements                   # ingredients (see element layout below)
	//   [u32 0][u32 0x1F4][u32 midFlag][u32 0]          # mid header (only midFlag varies: 0/1)
	//   0x3F1-list of u32                               # ref-id list (1-2 opaque ids)
	//   0x3F1-list of 17-u32 elements                   # results
	//   [u32 tail]                                      # @50
	//   [u8 trailingFlag]                               # @0x5c (0/1)
	// Element (18 u32 ingredient / 17 u32 result):
	//   [0]=[1]=itemId, [2]=0, [3]=flag, [4]=hash, [5..8]=[9..12]=item TEA key, [13..16]=const, ([17]=trailing, ing only)
	public static class TradeskillRecipeCodec
	{
		public const byte ListType = 3; // full recipe-book list (vs 1 = single RecipeLearnt)

		public const byte RoleIngredient = 0;
		public const byte RoleResult = 1;

		public const uint Opcode = (uint)GameServer0X2000Opcodes.TradeskillReplyIIR;
		public const uint Version = 1;
		private const uint HeaderTypeA = 0xF4241; // rdbType value A (1000001)
		private const uint HeaderTypeB = 0xF4242; // rdbType value B (1000002)

		// Fixed record/element constants (invariant across all 603 captured records; validated on parse so a capture
		// that breaks the assumption throws rather than silently mis-building).
		private const uint MidWord1 = 0x1F4; // mid[1]

		// This codec covers BOTH halves of the recipe-book exchange: the c2s TradeskillRequestIIR (0x06B9CD94, the
		// client opening the book - a trigger with no body of interest) and the s2c TradeskillReplyIIR (0x9062D559,
		// the catalog this codec builds). Opcode below is the reply (what BuildReplyIir emits).
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.TradeskillReplyIIR, GameServer0X2000Opcodes.TradeskillRequestIIR];

		// Elements [13..16]: two unset identity slots (NullIdentity.Pair).

		// Build the reply IIR body [env][version][type=3][count][records] from typed recipe records.
		public static byte[] BuildReplyBody(IReadOnlyCollection<RecipeRecord> recipes)
		{
			ArgumentNullException.ThrowIfNull(recipes);
			var body = new FaolanStream()
				.WriteByte(0x00) // env
				.WriteUInt32(Version)
				.WriteByte(ListType)
				.WriteUInt32(ListFrame.ForCount(recipes.Count));
			foreach (var r in recipes)
				WriteRecord(body, r);

			return body.ToArray();
		}

		// The full TradeskillReplyIIR carrier: [subop][0xC350][handle][reply body] (the create/craft window's recipe list).
		public static byte[] BuildReplyIir(uint handle, IReadOnlyCollection<RecipeRecord> recipes)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteArray(BuildReplyBody(recipes)).ToArray();
		}

		// Parse the reply IIR body (starting at the [u8 env] byte, i.e. just past [subop][0xC350][handle]) into typed
		// recipe records. Throws on a malformed stream or a broken layout invariant so a bad decode surfaces rather
		// than silently truncating / mis-building.
		public static List<RecipeRecord> ParseReplyBody(ReadOnlySpan<byte> body)
		{
			var p = 0;
			var env = body[p];
			p += 1;
			var version = Read(body, ref p);
			if (version != Version)
				throw new InvalidOperationException($"TradeskillReply: version {version} != {Version}");

			var type = body[p];
			p += 1;
			if (type != ListType)
				throw new InvalidOperationException($"TradeskillReply: type {type} != {ListType} (single-recipe form not supported)");

			_ = env;

			var count = ReadFramed(body, ref p);
			var recipes = new List<RecipeRecord>(count);
			for (var i = 0; i < count; i++)
				recipes.Add(ReadRecord(body, ref p));

			return recipes;
		}

		// Project a stored Recipe row (with its Items and their Item templates loaded) back into the wire record. The
		// per-element TEA key + hash live once on the referenced Items row (RecipeItem carries only id/flag/trailing).
		public static RecipeRecord ToRecord(Recipe row)
		{
			ArgumentNullException.ThrowIfNull(row);
			var refIds = new List<uint>
			{
				row.RefId0
			};
			if (row.RefId1.HasValue)
				refIds.Add(row.RefId1.Value);

			return new RecipeRecord(row.Id, row.Header1, row.RdbType, row.MidFlag, row.Tail, row.TrailingFlag, refIds, ElementsFor(row, RecipeItemRole.Ingredient),
				ElementsFor(row, RecipeItemRole.Result));
		}

		private static List<RecipeElement> ElementsFor(Recipe row, RecipeItemRole role)
		{
			return row
				.Items.Where(i => i.Role == role)
				.OrderBy(i => i.Position)
				.Select(i => new RecipeElement(i.ItemId, i.Flag, i.Item.Hash, i.Item.Key.Key1, i.Item.Key.Key2, i.Item.Key.Key3, i.Item.Key.Key4, i.Trailing))
				.ToList();
		}

		private static uint Read(ReadOnlySpan<byte> data, ref int p)
		{
			var v = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(p, 4));
			p += 4;
			return v;
		}

		private static RecipeElement ReadElement(ReadOnlySpan<byte> data, ref int p, bool hasTrailing)
		{
			var itemId = Read(data, ref p); //   [0] item id
			if (Read(data, ref p) != itemId) // [1] item id (dup)
				throw new InvalidOperationException($"TradeskillReply: element item-id words differ for {itemId:x}");
			if (Read(data, ref p) != 0) //       [2] = 0
				throw new InvalidOperationException($"TradeskillReply: element word[2] != 0 for {itemId:x}");

			var flag = Read(data, ref p); //     [3]
			var hash = Read(data, ref p); //     [4]
			var k1 = Read(data, ref p);
			var k2 = Read(data, ref p);
			var k3 = Read(data, ref p);
			var k4 = Read(data, ref p); //       [5..8] item TEA key
			if (Read(data, ref p) != k1 || Read(data, ref p) != k2 || Read(data, ref p) != k3 || Read(data, ref p) != k4)
				throw new InvalidOperationException($"TradeskillReply: element key-dup differs for {itemId:x}"); // [9..12]

			for (var c = 0; c < 4; c++)
			{
				if (Read(data, ref p) != NullIdentity.Pair[c])
					throw new InvalidOperationException($"TradeskillReply: element const word differs for {itemId:x}"); // [13..16]
			}

			var trailing = hasTrailing
				? Read(data, ref p)
				: 0u; // [17] (ingredients only)

			return new RecipeElement(itemId, flag, hash, k1, k2, k3, k4, trailing);
		}

		private static int ReadFramed(ReadOnlySpan<byte> body, ref int p)
		{
			return ListFrame.CountOf(Read(body, ref p));
		}

		private static RecipeRecord ReadRecord(ReadOnlySpan<byte> body, ref int p)
		{
			var listKey = Read(body, ref p);
			var head0 = Read(body, ref p);
			if (head0 != listKey)
				throw new InvalidOperationException($"TradeskillReply: recipe {listKey:x} header word0 {head0:x} != list-key");

			var header1 = Read(body, ref p);
			var rdbType = Read(body, ref p);
			if (rdbType != HeaderTypeA && rdbType != HeaderTypeB)
				throw new InvalidOperationException($"TradeskillReply: recipe {listKey:x} rdbType {rdbType:x} not in {{0x{HeaderTypeA:x},0x{HeaderTypeB:x}}}");

			var nIng = ReadFramed(body, ref p);
			var ingredients = new List<RecipeElement>(nIng);
			for (var e = 0; e < nIng; e++)
				ingredients.Add(ReadElement(body, ref p, true));

			if (Read(body, ref p) != 0 || Read(body, ref p) != MidWord1)
				throw new InvalidOperationException($"TradeskillReply: recipe {listKey:x} unexpected mid header");

			var midFlag = Read(body, ref p);
			if (Read(body, ref p) != 0)
				throw new InvalidOperationException($"TradeskillReply: recipe {listKey:x} unexpected mid trailer");

			var nRef = ReadFramed(body, ref p);
			var refIds = new List<uint>(nRef);
			for (var e = 0; e < nRef; e++)
				refIds.Add(Read(body, ref p));

			var nRes = ReadFramed(body, ref p);
			var results = new List<RecipeElement>(nRes);
			for (var e = 0; e < nRes; e++)
				results.Add(ReadElement(body, ref p, false));

			var tail = Read(body, ref p);
			var trailingFlag = body[p];
			p += 1;

			return new RecipeRecord(listKey, header1, rdbType, midFlag, tail, trailingFlag, refIds, ingredients, results);
		}

		private static void WriteElement(FaolanStream body, RecipeElement e, bool hasTrailing)
		{
			body
				.WriteUInt32(e.ItemId)
				.WriteUInt32(e.ItemId)
				.WriteUInt32(0)
				.WriteUInt32(e.Flag)
				.WriteUInt32(e.Hash)
				.WriteUInt32(e.Key1)
				.WriteUInt32(e.Key2)
				.WriteUInt32(e.Key3)
				.WriteUInt32(e.Key4) // key
				.WriteUInt32(e.Key1)
				.WriteUInt32(e.Key2)
				.WriteUInt32(e.Key3)
				.WriteUInt32(e.Key4) // key (dup)
				.WriteUInt32(NullIdentity.Pair[0])
				.WriteUInt32(NullIdentity.Pair[1])
				.WriteUInt32(NullIdentity.Pair[2])
				.WriteUInt32(NullIdentity.Pair[3]);
			if (hasTrailing)
				body.WriteUInt32(e.Trailing);
		}

		private static void WriteFramedCount(FaolanStream body, int n)
		{
			body.WriteUInt32(ListFrame.ForCount(n));
		}

		private static void WriteRecord(FaolanStream body, RecipeRecord r)
		{
			body
				.WriteUInt32(r.Id) //           list-key
				.WriteUInt32(r.Id) //           header[0] == list-key
				.WriteUInt32(r.Header1)
				.WriteUInt32(r.RdbType);
			WriteFramedCount(body, r.Ingredients.Count);
			foreach (var e in r.Ingredients)
				WriteElement(body, e, true);
			body.WriteUInt32(0).WriteUInt32(MidWord1).WriteUInt32(r.MidFlag).WriteUInt32(0); // mid x4
			WriteFramedCount(body, r.RefIds.Count);
			foreach (var id in r.RefIds)
				body.WriteUInt32(id);
			WriteFramedCount(body, r.Results.Count);
			foreach (var e in r.Results)
				WriteElement(body, e, false);
			body.WriteUInt32(r.Tail).WriteByte(r.TrailingFlag);
		}

		// A fully-decoded recipe: the per-recipe header/mid/tail fields, the opaque ref-id list, and the typed
		// ingredient/result elements. The list-key (== Id == header word 0) is written twice on the wire from Id.
		public sealed record RecipeRecord(
			uint Id,
			uint Header1,
			uint RdbType,
			uint MidFlag,
			uint Tail,
			byte TrailingFlag,
			IReadOnlyList<uint> RefIds,
			IReadOnlyList<RecipeElement> Ingredients,
			IReadOnlyList<RecipeElement> Results);

		// One ingredient (input) or result (output) item: the item id + its per-template TEA key (K1..K4) and hash,
		// plus the element Flag and (ingredients only) Trailing word. Role/position are implied by list membership.
		public readonly record struct RecipeElement(uint ItemId, uint Flag, uint Hash, uint Key1, uint Key2, uint Key3, uint Key4, uint Trailing);
	}
}
