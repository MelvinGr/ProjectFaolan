using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
	public static class TradeskillReplyCodec
	{
		public const byte ListType = 3; // full recipe-book list (vs 1 = single RecipeLearnt)

		public const byte RoleIngredient = 0;
		public const byte RoleResult = 1;

		public const uint Opcode = (uint)IirTypes.TradeskillReplyIIR;
		public const uint Version = 1;
		private const uint HeaderTypeA = 0xF4241; // rdbType value A (1000001)
		private const uint HeaderTypeB = 0xF4242; // rdbType value B (1000002)

		// Fixed record/element constants (invariant across all 603 captured records; validated on parse so a capture
		// that breaks the assumption throws rather than silently mis-building).
		private const uint MidWord1 = 0x1F4; // mid[1]

		// Elements [13..16]: two unset identity slots (NullIdentity.Pair).

		// CANONICAL - the only method here that writes bytes. The whole record: [subop][0xC350][handle][env][version]
		// [type=3][count][records] - the create/craft window's recipe list.
		public static byte[] Build(TradeskillReplyIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint handle, IReadOnlyCollection<RecipeRecord> recipes)
		{
			return Build(new TradeskillReplyIIR(handle, recipes));
		}

		// Throws on a malformed stream or a broken layout invariant so a bad decode surfaces rather than silently
		// truncating / mis-building.
		public static TradeskillReplyIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeskillReplyCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeskillReplyIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "TradeskillReplyIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();

			return new TradeskillReplyIIR(handle, ReadRecords(ref r));
		}

		internal static void Write(FaolanStream s, TradeskillReplyIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle);
			WriteRecords(s, v.Recipes);
		}

		// The reply BODY alone (from the [u8 env] byte on) - what the importer's captured slices carry, because the
		// harvest cuts the packet at the carrier prefix rather than at the record.
		internal static List<RecipeRecord> ReadRecords(ref SpanReader r)
		{
			r.ExpectU8(0x00, "env");
			r.ExpectU32Be(Version, "reply version");
			r.ExpectU8(ListType, "reply type (only the full book list is modelled)");

			var count = r.ReadListCount();
			var recipes = new List<RecipeRecord>(count);
			for (var i = 0; i < count; i++)
				recipes.Add(ReadRecord(ref r));

			return recipes;
		}

		internal static void WriteRecords(FaolanStream s, IReadOnlyCollection<RecipeRecord> recipes)
		{
			ArgumentNullException.ThrowIfNull(recipes);
			s.WriteByte(0x00).WriteUInt32(Version).WriteByte(ListType).WriteUInt32(ListFrame.ForCount(recipes.Count));
			foreach (var r in recipes)
				WriteRecord(s, r);
		}

		private static RecipeElement ReadElement(ref SpanReader r, bool hasTrailing)
		{
			var itemId = r.ReadU32Be(); //           [0] item id
			r.ExpectU32Be(itemId, "element item-id duplicate"); // [1] item id (dup)
			r.ExpectU32Be(0, "element word[2]"); //  [2] = 0

			var flag = r.ReadU32Be(); //             [3]
			var hash = r.ReadU32Be(); //             [4]
			var k1 = r.ReadU32Be();
			var k2 = r.ReadU32Be();
			var k3 = r.ReadU32Be();
			var k4 = r.ReadU32Be(); //               [5..8] item TEA key
			r.ExpectU32Be(k1, "element key-dup [9]"); //  [9..12]
			r.ExpectU32Be(k2, "element key-dup [10]");
			r.ExpectU32Be(k3, "element key-dup [11]");
			r.ExpectU32Be(k4, "element key-dup [12]");

			for (var c = 0; c < 4; c++)
				r.ExpectU32Be(NullIdentity.Pair[c], $"element const word [{13 + c}]"); // [13..16]

			var trailing = hasTrailing
				? r.ReadU32Be()
				: 0u; // [17] (ingredients only)

			return new RecipeElement(itemId, flag, hash, k1, k2, k3, k4, trailing);
		}

		private static RecipeRecord ReadRecord(ref SpanReader r)
		{
			var listKey = r.ReadU32Be();
			r.ExpectU32Be(listKey, "recipe header word0 (== the list key)");

			var header1 = r.ReadU32Be();
			var rdbType = r.ReadU32Be();
			if (rdbType != HeaderTypeA && rdbType != HeaderTypeB)
				throw new CodecParseException($"TradeskillReplyCodec: recipe {listKey:x} rdbType {rdbType:x} not in {{0x{HeaderTypeA:x},0x{HeaderTypeB:x}}}");

			var nIng = r.ReadListCount();
			var ingredients = new List<RecipeElement>(nIng);
			for (var e = 0; e < nIng; e++)
				ingredients.Add(ReadElement(ref r, true));

			r.ExpectU32Be(0, "recipe mid header word0");
			r.ExpectU32Be(MidWord1, "recipe mid header word1");
			var midFlag = r.ReadU32Be();
			r.ExpectU32Be(0, "recipe mid trailer");

			var nRef = r.ReadListCount();
			var refIds = new List<uint>(nRef);
			for (var e = 0; e < nRef; e++)
				refIds.Add(r.ReadU32Be());

			var nRes = r.ReadListCount();
			var results = new List<RecipeElement>(nRes);
			for (var e = 0; e < nRes; e++)
				results.Add(ReadElement(ref r, false));

			return new RecipeRecord(listKey, header1, rdbType, midFlag, r.ReadU32Be(), r.ReadU8(), refIds, ingredients, results);
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

	}

	// TradeskillReplyIIR (0x9062D559) - the whole recipe book for one character handle.
	public sealed record TradeskillReplyIIR(uint Handle, IReadOnlyCollection<RecipeRecord> Recipes);

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

	// The stored `Recipe` row -> wire record projection. A MAPPER, not a codec (#533): the per-element TEA key + hash
	// live once on the referenced `Items` row (RecipeItem carries only id/flag/trailing).
	public static class RecipeRecords
	{
		public static RecipeRecord FromRow(Recipe row)
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
	}
}
