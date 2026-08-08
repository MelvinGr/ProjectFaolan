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
		/// </summary>
		public static readonly ImmutableDictionary<uint, HarvestNode> Nodes = new HarvestNode[]
		{
			// --- Cloth --------------------------------------------------------------------------------------------
			new(3237565, "Cotton Shrub", 161192), // Cotton
			// --- Wood ---------------------------------------------------------------------------------------------
			new(3237645, "Ash Tree", 161225), // Ash
			new(3237646, "Yew Tree", 161218), // Yew
			new(3237647, "Oak Tree", 161213), // Oak       - measured
			// ⚠️ Birch sits at 3927910, OUTSIDE the 1611xx band every other resource is in - it was harvested from a
			// different capture. Included because it is structurally the same kind of row (Value 0, no description,
			// hashed, name matches the node exactly), but it is the one entry whose id space does not corroborate it.
			new(3935376, "Birch Tree", 3927910), // Birch
			new(3935377, "Elm Tree", null), // no item named Elm ships
			new(3935378, "Ironwood", null), // no item named Ironwood ships
			// --- Stone --------------------------------------------------------------------------------------------
			new(3237641, "Sandstone", 161207), // Sandstone
			new(3237642, "Granite Rock", 161206), // Granite   - measured
			new(3237644, "Basalt Rock", 161205), // Basalt
			new(3935379, "Greenstone Formation", null), // no item named Greenstone ships
			new(3935380, "Marble", null), // no item named Marble ships
			new(3935381, "Slate Rock", null), // no item named Slate ships
			// --- Metal --------------------------------------------------------------------------------------------
			new(3237648, "Copper Node", 161197), // Copper
			new(3237652, "Iron Node", 161196), // Iron      - measured
			new(3237653, "Duskmetal Node", 161195), // Duskmetal
			new(3237656, "Silver Vein", 161202), // Silver
			new(3237657, "Electrum Vein", 161201), // Electrum
			new(3237659, "Gold Vein", 161200), // Gold
			new(3935343, "Lead Deposit", 3927913), // Lead      - same off-band id space as Birch
			new(3935344, "Nickel Deposit", null), // no item named Nickel ships
			new(3935345, "Chromium Deposit", null), // no item named Chromium ships
			new(3935373, "Pyrite Lode", null), // no item named Pyrite ships
			new(3935374, "Bismuth Lode", null), // no item named Bismuth ships
			new(3935375, "Mithrite Lode", null) // no item named Mithrite ships
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
	///     One harvest node TYPE: the LDB 50200 id the client resolves its name through, that name, and the item a
	///     swing grants (null when no such item ships — see <see cref="HarvestCatalog.Nodes" />).
	/// </summary>
	public sealed record HarvestNode(uint DisplayId, string Name, uint? ResourceItemId);
}
