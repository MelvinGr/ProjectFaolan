using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     The recipe-book list carried in <c>TradeskillReplyIIR_c</c>, the server's answer to the client's
	///     <c>TradeskillRequestIIR_c</c> when the Crafting - Recipes window opens.
	/// </summary>
	/// <remarks>
	///     Grammar recovered from AgeOfConan.exe (client readers FUN_007aab30 into FUN_007abb30 into the per-record
	///     FUN_0077be70; the 4-byte reader is FUN_0048f900, big-endian) and validated byte-exact against
	///     NecroL80_..._open-crrafting-receipies frame 2926 (603 recipes, 209309 bytes consumed exactly). See
	///     docs/subsystems/tradeskill-recipes.md.
	///     <para>
	///         The record is fully decoded into typed fields (<c>RecipeRecord</c>) with no verbatim blob: every wire word
	///         is either a per-recipe column, a per-element field carried by the item (<c>RecipeItem</c> plus its
	///         <c>Items</c> row's TEA key and hash), or one of the fixed constants below. <c>ParseReplyBody</c> and
	///         <c>BuildReplyBody</c> round-trip byte-for-byte (<c>TradeskillRecipeCodecTests</c>), so the record can be
	///         rebuilt entirely from the DB columns (<c>ToRecord</c>), which is how the live server answers the request.
	///     </para>
	///     <para>
	///         The reply IIR body, after the <c>[subop][entityClass][handle]</c> carrier prefix:
	///         <code>
	///         [u8 env=0][u32 version=1][u8 type][u32 c]   # for type 3, c is the list frame over the recipe count
	///         recipes x record                            # type 1 = a single RecipeLearnt (not modelled here)
	///
	///         record:
	///           [u32 listKey]                             # the recipe id (the map key, and header word 0)
	///           [u32 id=listKey][u32 header1][u32 rdbType]# header (the tradeskill RDB type)
	///           list of 18-u32 elements                   # ingredients
	///           [u32 0][u32 duration][u32 midFlag][u32 0] # mid header (duration in centiseconds)
	///           list of u32                               # ref-id list (1-2 opaque ids)
	///           list of 17-u32 elements                   # results
	///           [u32 tail]                                # @50
	///           [u8 trailingFlag]                         # @0x5c (0/1)
	///
	///         element (18 u32 ingredient / 17 u32 result):
	///           [0]=[1]=itemId, [2]=0, [3]=flag, [4]=hash, [5..8]=[9..12]=item TEA key, [13..16]=const,
	///           ([17]=trailing, ingredients only)
	///         </code>
	///     </para>
	/// </remarks>
	public static class TradeskillReplyCodec
	{
		public const byte ListType = 3; // full recipe-book list (vs 1 = single RecipeLearnt)

		public const uint Opcode = (uint)IirTypes.TradeskillReplyIIR;
		public const uint Version = 1;
		// There is no fixed set of rdbType values, and asserting one refuses retail traffic: NecroL80's two captures carry
		// {0xF4241, 0xF4242} while the DemoL80 capture uses 0xF4244 (1000004) for all 45 of its records - one type per
		// capture, not a shared vocabulary. The word is carried on the record and round-trips either way, so a gate on it
		// would only cost coverage: it could not catch a mis-parse, because a wrong split would not land on a plausible RDB
		// type by luck.

		// Fixed record and element constants (invariant across all 603 captured records; validated on parse so a capture
		// that breaks the assumption throws rather than silently mis-building).
		//
		// mid[1] is the recipe's own craft duration in centiseconds rather than a filler constant. It lands on
		// `GameData::ClientTradeskillRecipe_c` at `+0x24` (record reader `FUN_0077be70`), and the client's command-queue
		// duration getter for `TradeskillCommand_c` (`FUN_0114bf50`) returns exactly that member for a craft - the value that
		// sizes its `TradeskillProgressBarDesc` bar. The same getter hardcodes 500 for a gem add, 0 for a gem remove and 100
		// for anything else, so the unit is shared with the BCC's `activation_time`/`recharge_time`: centiseconds. 0x1F4 is
		// 5.00 s.
		//
		// It stays a folded constant rather than a `Recipe` column because all 603 captured recipes carry the one value -
		// there is no second value to store. Columnize it the day a capture shows one; `Parse` throws on anything else, so
		// that day cannot pass unnoticed.
		public const uint CraftDurationCentiseconds = 0x1F4; // mid[1]

		// Elements [13..16]: two unset identity slots (NullIdentity.Pair).

		// Canonical: the only method here that writes bytes. The whole record - [subop][entityClass][handle][env][version]
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

		// Throws on a malformed stream or a broken layout invariant so a bad decode surfaces rather than silently truncating
		// or mis-building.
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

		// The reply body alone, from the [u8 env] byte on - what the importer's captured slices carry, because the harvest
		// cuts the packet at the carrier prefix rather than at the record.
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
			var itemId = r.ReadU32Be(); //           [0] ACG template1
			var itemId2 = r.ReadU32Be(); //          [1] ACG template2 - equal to [0] on all but one captured element
			r.ExpectU32Be(0, "element word[2]"); //  [2] = 0

			var flag = r.ReadU32Be(); //             [3]
			var hash = r.ReadU32Be(); //             [4]
			var k1 = r.ReadU32Be();
			var k2 = r.ReadU32Be();
			var k3 = r.ReadU32Be();
			var k4 = r.ReadU32Be(); //               [5..8] template1's TEA key
			var j1 = r.ReadU32Be(); //               [9..12] template2's TEA key - the SAME key when the pair is
			var j2 = r.ReadU32Be(); //                       one template twice, which is all but one element
			var j3 = r.ReadU32Be();
			var j4 = r.ReadU32Be();

			for (var c = 0; c < 4; c++)
				r.ExpectU32Be(NullIdentity.Pair[c], $"element const word [{13 + c}]"); // [13..16]

			var trailing = hasTrailing
				? r.ReadU32Be()
				: 0u; // [17] (ingredients only)

			return new RecipeElement(itemId, flag, hash, k1, k2, k3, k4, trailing, itemId2,
				j1 == k1 && j2 == k2 && j3 == k3 && j4 == k4 ? 0 : j1, j2, j3, j4);
		}

		private static RecipeRecord ReadRecord(ref SpanReader r)
		{
			var listKey = r.ReadU32Be();
			r.ExpectU32Be(listKey, "recipe header word0 (== the list key)");

			var header1 = r.ReadU32Be();
			var rdbType = r.ReadU32Be();

			var nIng = r.ReadListCount();
			var ingredients = new List<RecipeElement>(nIng);
			for (var e = 0; e < nIng; e++)
				ingredients.Add(ReadElement(ref r, true));

			r.ExpectU32Be(0, "recipe mid header word0");
			r.ExpectU32Be(CraftDurationCentiseconds, "recipe mid header word1 (the craft duration)");
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
				.WriteUInt32(e.ItemId2 == 0 ? e.ItemId : e.ItemId2)
				.WriteUInt32(0)
				.WriteUInt32(e.Flag)
				.WriteUInt32(e.Hash)
				.WriteUInt32(e.Key1)
				.WriteUInt32(e.Key2)
				.WriteUInt32(e.Key3)
				.WriteUInt32(e.Key4) // template1's key
				.WriteUInt32(e.Item2Key1 == 0 ? e.Key1 : e.Item2Key1)
				.WriteUInt32(e.Item2Key1 == 0 ? e.Key2 : e.Item2Key2)
				.WriteUInt32(e.Item2Key1 == 0 ? e.Key3 : e.Item2Key3)
				.WriteUInt32(e.Item2Key1 == 0 ? e.Key4 : e.Item2Key4) // template2's key
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
				.WriteUInt32(r.ProfessionId);
			WriteFramedCount(body, r.Ingredients.Count);
			foreach (var e in r.Ingredients)
				WriteElement(body, e, true);
			body.WriteUInt32(0).WriteUInt32(CraftDurationCentiseconds).WriteUInt32(r.MidFlag).WriteUInt32(0); // mid x4
			WriteFramedCount(body, r.RefIds.Count);
			foreach (var id in r.RefIds)
				body.WriteUInt32(id);
			WriteFramedCount(body, r.Results.Count);
			foreach (var e in r.Results)
				WriteElement(body, e, false);
			body.WriteUInt32(r.Tail).WriteByte(r.TrailingFlag);
		}

	}

	/// <summary>The whole recipe book for one character handle.</summary>
	public sealed record TradeskillReplyIIR(uint Handle, IReadOnlyCollection<RecipeRecord> Recipes);

	/// <summary>
	///     A fully decoded recipe: the per-recipe header, mid and tail fields, the opaque ref-id list, and the typed
	///     ingredient and result elements. The list key (the Id, and header word 0) is written twice on the wire from Id.
	/// </summary>
	public sealed record RecipeRecord(
		uint Id,
		uint Header1,
		uint ProfessionId,
		uint MidFlag,
		uint Tail,
		byte TrailingFlag,
		IReadOnlyList<uint> RefIds,
		IReadOnlyList<RecipeElement> Ingredients,
		IReadOnlyList<RecipeElement> Results);

	/// <summary>
	///     One ingredient (input) or result (output) item: the item id plus its per-template TEA key (K1..K4) and hash,
	///     plus the element Flag and, for ingredients only, the Trailing word. Role and position are implied by list
	///     membership.
	/// </summary>
	/// <remarks>
	///     <c>ItemId</c> and <c>ItemId2</c> are the ACG template pair (<c>ACGItemTemplateID</c> /
	///     <c>ACGItemTemplateID2</c>), not an id and a redundant copy of it - see the <c>ProtoAcgItem</c> notes. They
	///     agree on 2411 of the 2412 captured elements, and the one that does not (0x3CAE32 / 0x3CAE33, consecutive as a
	///     pair's two variants are) is why the second must not be asserted to mirror the first. <c>ItemId2</c> is 0 for a
	///     row built from the DB, where only one template is stored, and the writer then mirrors the first.
	///     <para>
	///         The two 4-word TEA keys are one per template (<c>keyN</c> to <c>templateN</c>), which is why the second
	///         stops mirroring the first on exactly the element whose templates differ. <c>Item2Key1 == 0</c> means "the
	///         same key", the only shape a DB-built row can have and what 2411 of 2412 captured elements carry.
	///     </para>
	/// </remarks>
	public readonly record struct RecipeElement(uint ItemId, uint Flag, uint Hash, uint Key1, uint Key2, uint Key3, uint Key4, uint Trailing, uint ItemId2 = 0,
		uint Item2Key1 = 0, uint Item2Key2 = 0, uint Item2Key3 = 0, uint Item2Key4 = 0);

	/// <summary>
	///     The stored <c>Recipe</c> row to wire record projection. A mapper, not a codec: the per-element TEA key and hash
	///     live once on the referenced <c>Items</c> row, <c>RecipeItem</c> carrying only id, flag and trailing.
	/// </summary>
	public static class RecipeRecords
	{
		public static RecipeRecord FromRow(Recipe row)
		{
			ArgumentNullException.ThrowIfNull(row);
			var refIds = new List<uint>
			{
				row.RequiredFeatId
			};
			if (row.RequiredWorkshopFeatId.HasValue)
				refIds.Add(row.RequiredWorkshopFeatId.Value);

			return new RecipeRecord(row.Id, row.Header1, row.ProfessionId, row.MidFlag, row.Tail, row.TrailingFlag, refIds, ElementsFor(row, RecipeItemRole.Ingredient),
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
