using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Resource harvesting (#494): which item a harvest node yields, and how much.
	/// </summary>
	/// <remarks>
	///     Harvest nodes reach the client as <c>DestructibleFullUpdateIIR</c> (<c>0x6BD56736</c>) world objects with a
	///     <b>max health of 1000</b> — that value is the category discriminator, separating them from guild-city pieces
	///     at 1,000,000 and up (#498). The payoff needs no new protocol at all: retail grants a <b>stack of the matching
	///     resource item</b> through the ordinary item path, <c>DispatchAddItem</c> into the backpack plus a
	///     <c>DispatchDisplayItemLootChatMessage</c> carrying <c>[itemId][itemId][0][count]</c>.
	///     <para>
	///         Three rows are directly measured from the mining span of the chat-annotated capture, each landing beside
	///         its own annotation: <b>Granite</b> (`just mined`), <b>Oak</b> (`gethering wood`) and <b>Iron</b>
	///         (`mining iron`). Every other row is the same rule applied through an exact NAME correspondence — the
	///         shipped item family 161190-161225 (Ironsilk, Silk, Cotton, Duskmetal, Iron, Copper, Gold, Electrum,
	///         Silver, Basalt, Granite, Sandstone, Oak, Yew, Ash) matches the node names one-for-one, so "Yew Tree" →
	///         Yew is a lookup rather than a guess.
	///     </para>
	///     <para>
	///         ⚠️ The <b>yield</b> is 1 unit per swing, not the 40 / 60 / 40 the earlier captures suggested — those were
	///         STACK TOTALS after many swings. See <see cref="Yields" />. Depletion is modelled (#732 tracks the rest:
	///         regrowth in place, the mine-triggered ambush, the rare cross-drop).
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5]
	///     </para>
	///     <para>
	///         ⚠️ <b>The key is the node's DISPLAY id, not its name (#734).</b> A destructible record carries two
	///         names: a <c>name</c> STRING and a <c>DisplayId</c> the client resolves through LDB category
	///         <see cref="NodeDisplayCategory" />, and it is the LDB one that appears in game. The wire string is
	///         internal, and retail itself has it wrong on several nodes — the same silver vein ships as
	///         <i>Martial Ring</i>, a copper node as <i>Vitalic Bronze-studded Armbands</i>, an ash tree as
	///         <i>Bloodsilver-studded Gloves</i>, a cotton shrub as <i>Blacksmithing Hammer TESTITEM</i>, and three
	///         more as raw <c>NPC_Global_leather_…</c> asset names. Keying on the string made every one of those
	///         silently un-harvestable: <c>ResourceFor</c> returned null and the interaction just did nothing.
	///         The display id is stable content identity, so it is what the rows below are keyed by, and the
	///         <see cref="HarvestNode.Name" /> beside each one is documentation (and the text the server logs and
	///         says), read straight out of the client's own LDB.
	///     </para>
	/// </remarks>
	public static class HarvestCatalog
	{
		/// <summary>Max health every harvest node carries, and what identifies one as harvestable on the wire.</summary>
		public const uint NodeMaxHealth = 1000;

		/// <summary>The LDB category a node's displayed name resolves through — the client's own item/dynel names.</summary>
		public const uint NodeDisplayCategory = 50200;

		/// <summary>
		///     A node's health bottoms out at <b>1</b>, never 0 — measured across every capture — and the node then
		///     despawns. So "exhausted" is <c>health &lt;= ExhaustedHealth</c>, not <c>== 0</c>.
		///     ⚠️ Retail's own floor is softer than this: the annotated session leaves a gold vein at 31 and a basalt
		///     rock at 28, both of which then regrow, so a node below one swing's worth is not necessarily finished.
		///     Whether a swing can be paid out of a partial pool is NOT measured — nobody swung at a node holding less
		///     than <see cref="SwingCost" /> in any capture — so we require a full swing's worth and stop there rather
		///     than invent a partial payout. [V0: whether a node under 100 health can still be harvested]
		/// </summary>
		public const uint ExhaustedHealth = 1;

		/// <summary>
		///     What ONE swing takes off a node, measured at exactly 100 of the 1000 max — so a full node is ten swings.
		///     The client renders <c>health / max</c> as the PERCENTAGE in the node's health bar, which is what the
		///     annotated mining session narrates: "next to a 30% gold vein now", then "-10% each time", against a node
		///     the wire walks 298 → 198 → 98.
		///     [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5, dynel 0x2688]
		/// </summary>
		public const uint SwingCost = 100;

		/// <summary>
		///     What one swing GRANTS: exactly one unit. Measured on every harvest add in both mining captures
		///     (<c>stat_list</c> 26 = 1) and corroborated by the resource stack ticking up by one per swing, and by the
		///     annotations ("also -10 %, got 1"; "got 3" after three swings on the gold vein).
		/// </summary>
		public const uint YieldPerSwing = 1;

		/// <summary>The yield used for a node whose count no capture has measured. The modal observed value.</summary>
		public const uint DefaultYield = YieldPerSwing;

		/// <summary>
		///     Every harvest node type that ships in <c>WorldObjects</c>, by its LDB <see cref="NodeDisplayCategory" />
		///     display id. A null <see cref="HarvestNode.ResourceItemId" /> is a node we can render and deplete but
		///     cannot pay out, because no matching item exists in <c>faolan.db</c> — listed rather than omitted, so the
		///     gap is visible here instead of living in a test allowlist, and so the server can name the node correctly
		///     when it refuses. Inventing an id would hand out the wrong resource.
		///     <para>
		///         ⚠️ Each null nevertheless names its item ID, and that is not a guess: the client's own item-name LDB
		///         (category 50200, plaintext) puts the whole family in one contiguous block —
		///         Slate 3927907, Marble 3927908, Greenstone 3927909, <b>Birch 3927910</b>, Elm 3927911,
		///         Ironwood 3927912, <b>Lead 3927913</b>, Nickel 3927914, Chromium 3927915, <b>Pyrite 3927918</b>,
		///         Mithrite 3927920, Bismuth 3927927. The three we already hold sit INSIDE that run, which is what
		///         corroborates it. Reproduce with <c>--report ldb-search --report-arg 50200:Ironwood</c> (#746).
		///     </para>
		///     <para>
		///         So the gap is NOT "the client does not ship them". Every id above is stated by the client's own LDB
		///         (verified one by one with <c>--report ldb-search --report-arg 50200:&lt;name&gt;</c>), and every one
		///         of the nine has an RDB item template at that id (type 1000020, ~140 B, local) — it is
		///         <b>encrypted</b>, and the TEA key is the item's own <c>ProtoDecryptionKey</c>, which only ever
		///         arrives on the WIRE. No capture holds these resources, so there is no key and the template stays
		///         opaque (Gitea #203).
		///     </para>
		///     <para>
		///         ⚠️ <b>The blocker is the KEY, not the row's contents.</b> Two things a row would be missing are red
		///         herrings: the ACG <c>Hash</c> is minted at send time by <c>AcgHash.ForWire</c> and a 0 there means
		///         "not harvested", not "cannot be held" (proven in-client, #723); <c>Level</c> is per-TIER and only
		///         reaches the loot-chat message (Copper 20, Iron 40, Gold 60), so a wrong one is cosmetic. What
		///         actually stops a row being shipped is that the client cannot LOAD a keyless item: an item template
		///         with no decryption key fails outright in <c>RDBSource.cpp:352</c> ("Resource %u:%u does not have a
		///         decryption key") and is never fetched from the server. All 4,749 items we ship carry a key — a
		///         keyless one has never existed — so minting these would hand the player an item the client cannot
		///         render. Wiring one is a one-line change the moment a source supplies its key.
		///     </para>
		///     <para>
		///         Sources checked and exhausted for these nine: the capture corpus (they are in none of it, which is
		///         why the item importer's seven harvest sources produce nothing), and the <c>Archive/</c> p_items
		///         dumps from the original Faolan project (not one of the nine appears in any of the three). The one
		///         live route left is Gitea #751 — <c>itemref://</c> chat links, which carry a key quad for items the
		///         capturing character never held.
		///     </para>
		/// </summary>
		public static readonly ImmutableDictionary<uint, HarvestNode> Nodes = new HarvestNode[]
		{
			// --- Cloth (Weaver, cloth_tier1) ----------------------------------------------------------------------
			new(3237565, "Cotton Shrub", 161192), // Cotton
			// --- Wood (Woodcutting) -------------------------------------------------------------------------------
			new(3237645, "Ash Tree", 161225), // Ash        - wood_tier1
			new(3237646, "Yew Tree", 161218), // Yew        - wood_tier2
			new(3237647, "Oak Tree", 161213), // Oak        - wood_tier3, measured
			// ⚠️ Birch sits at 3927910, OUTSIDE the 1611xx band every other resource is in - it was harvested from a
			// different capture. Included because it is structurally the same kind of row (Value 0, no description,
			// hashed, name matches the node exactly), but it is the one entry whose id space does not corroborate it.
			new(3935376, "Birch Tree", 3927910), // Birch   - wood_tier1
			new(3935377, "Elm Tree", null), // Elm is item 3927911 - no row, see the id block below (wood_tier2)
			new(3935378, "Ironwood", null), // Ironwood is item 3927912 (wood_tier3)
			// --- Stone (Stonecutting) -----------------------------------------------------------------------------
			new(3237641, "Sandstone", 161207), // Sandstone - stone_tier1
			new(3237642, "Granite Rock", 161206), // Granite - stone_tier2, measured
			new(3237644, "Basalt Rock", 161205), // Basalt   - stone_tier3
			new(3935379, "Greenstone Formation", null), // Greenstone is item 3927909 (stone_tier3)
			// ⭐ THE ONE PROVEN REQUIREMENT (#779): retail refused a Stonecutting APPRENTICE this node and named
			// `Stonecutting Adept` (feat template 894305) as what was missing. Every other row's requirement is
			// deliberately null - see <see cref="HarvestNode.RequiredFeatId" />.
			new(3935380, "Marble", null, 894305), // Marble is item 3927908 (stone_tier2)
			new(3935381, "Slate Rock", null), // Slate is item 3927907 (stone_tier1)
			// --- Metal (Mining) -----------------------------------------------------------------------------------
			new(3237648, "Copper Node", 161197), // Copper  - iron_tier1
			new(3237652, "Iron Node", 161196), // Iron      - iron_tier2, measured
			new(3237653, "Duskmetal Node", 161195), // Duskmetal - iron_tier3
			new(3935343, "Lead Deposit", 3927913), // Lead  - iron_tier1, same off-band id space as Birch
			new(3935344, "Nickel Deposit", null), // Nickel is item 3927914 (iron_tier2)
			new(3935345, "Chromium Deposit", null), // Chromium is item 3927915 (iron_tier3)
			// --- Precious metal (Prospecting) ---------------------------------------------------------------------
			new(3237656, "Silver Vein", 161202), // Silver  - gold_tier1
			new(3237657, "Electrum Vein", 161201), // Electrum - gold_tier2
			new(3237659, "Gold Vein", 161200), // Gold      - gold_tier3
			// Pyrite was in the gap list until 2026-08-09: item 3927918 shipped all along but NAMELESS, so the gap
			// check (which matches on the name) could not see it. `rdb-items` decrypting its template is what made it
			// findable - the same off-band id space as Birch and Lead, one row further along.
			new(3935373, "Pyrite Lode", 3927918), // Pyrite - gold_tier1
			new(3935374, "Bismuth Lode", null), // Bismuth is item 3927927 (gold_tier2)
			new(3935375, "Mithrite Lode", null) // Mithrite is item 3927920 (gold_tier3)
		}.ToImmutableDictionary(n => n.DisplayId);

		/// <summary>
		///     Node display id -> a measured stack size, for any node whose count differs from
		///     <see cref="YieldPerSwing" />. <b>Empty, and that is the finding.</b>
		///     <para>
		///         It used to hold Granite 40 / Oak 60 / Iron 40, read off the 4th u32 of the
		///         <c>DispatchDisplayItemLootChatMessage</c> that follows each harvest. That word is the item's
		///         <b>LEVEL</b>, not a count: the body is the same <c>ProtoAcgItem</c>-shaped layout the client's
		///         <c>itemref://</c> chat link carries — <c>[template][template][0][level][hash][key1][key2][key3]</c>,
		///         with <c>key3</c> the <c>NullIdentity</c> sentinel — and <c>Items.Level</c> for Granite, Oak and Iron
		///         is 40, 60 and 40 exactly. The real count rides the add's own <c>stat_list</c> 26, and it is 1 on
		///         every captured harvest.
		///     </para>
		/// </summary>
		public static readonly ImmutableDictionary<uint, uint> Yields = ImmutableDictionary.Create<uint, uint>();

		/// <summary>
		///     The node type a world object is, or null when it is not a harvest node. Both halves of the display
		///     reference are required: an LDB id is only unique WITHIN its category, and the guild-city destructibles
		///     next door carry category 50050 with ids as low as 73.
		/// </summary>
		public static HarvestNode? NodeFor(uint? displayCategory, uint? displayId) =>
			displayCategory == NodeDisplayCategory && displayId is { } id && Nodes.TryGetValue(id, out var node) ? node : null;

		/// <summary>The resource a node yields, or null when the object is not a harvest node we can pay out.</summary>
		public static uint? ResourceFor(uint? displayCategory, uint? displayId) => NodeFor(displayCategory, displayId)?.ResourceItemId;

		/// <summary>How much a node yields per harvest.</summary>
		public static uint YieldFor(uint? displayId) =>
			displayId is { } id && Yields.TryGetValue(id, out var count) ? count : DefaultYield;

		/// <summary>
		///     Whether this world object is a harvestable resource node. A node whose resource item does not ship is
		///     NOT harvestable — it stays in the world, inert, rather than granting something wrong.
		/// </summary>
		public static bool IsHarvestable(uint? displayCategory, uint? displayId) => ResourceFor(displayCategory, displayId) != null;

		/// <summary>The node's displayed name, for logs and the server's own messages; null when it is not a node.</summary>
		public static string? NameFor(uint? displayCategory, uint? displayId) => NodeFor(displayCategory, displayId)?.Name;
	}

	/// <summary>
	///     One harvest node TYPE: the LDB 50200 id the client resolves its name through, that name, the item a
	///     swing grants (null when no such item ships — see <see cref="HarvestCatalog.Nodes" />), and the gathering
	///     rank it demands.
	/// </summary>
	/// <param name="RequiredFeatId">
	///     The feat TEMPLATE id (a <c>Feats.Id</c>) a character must have reached for retail to let them harvest this
	///     node, or null when no capture states one — in which case the node is ungated, which is what the server did
	///     for every node before #779.
	///     <para>
	///         ⚠️ <b>Exactly one row is non-null, and filling the rest would be an invention.</b> Retail's refusal
	///         (<c>FeatRequired</c>, naming the missing feat) appears twice in the whole corpus and both times it is
	///         the same node and the same feat: Marble → <c>Stonecutting Adept</c>. The obvious extrapolation — read
	///         the rank off the node's ART TIER — does not survive contact with the data: the client's own mesh names
	///         group the 25 node types into <b>five families × three tiers</b>, each tier shared by exactly TWO node
	///         types (<c>006468_resource_stone_tier2_node2</c> is the mesh of BOTH Granite Rock and Marble), so the
	///         tier cannot separate the pair, and only one member of one pair has a measured requirement. The family
	///         does name the PROFESSION cleanly (wood → Woodcutting, stone → Stonecutting, iron → Mining, gold →
	///         Prospecting, cloth → Weaver), which is why each row above carries its family and tier as a comment;
	///         that is the scaffold the remaining 24 rows drop into the moment another refusal is captured.
	///     </para>
	///     <para>
	///         The bounds the corpus does give, for whoever fills these in: <b>NecroL80</b> holds Master on every
	///         gathering ladder and harvested Granite, Oak, Iron, Copper, Silver, Electrum, Gold, Basalt and Ironsilk,
	///         so all nine are ≤ Master (loose). <b>DemoL80</b> holds Prospecting Adept / Stonecutting Apprentice /
	///         Woodcutting Apprentice / Mining Novice and harvested <b>Pyrite</b>, so gold_tier1 is ≤ Adept — and was
	///         refused Marble at Apprentice, which is the equality above.
	///         [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json]
	///     </para>
	/// </param>
	public sealed record HarvestNode(uint DisplayId, string Name, uint? ResourceItemId, uint? RequiredFeatId = null);
}
