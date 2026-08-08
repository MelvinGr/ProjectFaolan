using System.Collections.Generic;
using System.Collections.Immutable;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Resource harvesting (#494): which item a harvest node yields, and how much.
	/// </summary>
	/// <remarks>
	///     Harvest nodes reach the client as <c>DestructibleFullUpdateIIR</c> (<c>0x6BD56736</c>) world objects with a
	///     <b>max health of 1000</b> — that value is the category discriminator, separating them from guild-city pieces
	///     at 1,000,000 (#498). The payoff needs no new protocol at all: retail grants a <b>stack of the matching
	///     resource item</b> through the ordinary item path, <c>DispatchAddItem</c> into the backpack plus a
	///     <c>DispatchDisplayItemLootChatMessage</c> carrying <c>[itemId][itemId][0][count]</c>.
	///     <para>
	///         Three rows are directly measured from the mining span of the chat-annotated capture, each landing beside
	///         its own annotation: <b>Granite ×40</b> (`just mined`), <b>Oak ×60</b> (`gethering wood`) and
	///         <b>Iron ×40</b> (`mining iron`). Every other row is the same rule applied through an exact NAME
	///         correspondence — the shipped item family 161190-161225 (Ironsilk, Silk, Cotton, Duskmetal, Iron, Copper,
	///         Gold, Electrum, Silver, Basalt, Granite, Sandstone, Oak, Yew, Ash) matches the node names the captures
	///         stream one-for-one, so "Yew Tree" → Yew is a lookup rather than a guess.
	///     </para>
	///     <para>
	///         ⚠️ The <b>yield</b> here is WRONG, and the counts below are not per-harvest. The annotated mining
	///         session measures a swing at exactly <b>1 unit</b> for exactly <b>100 health</b>, so a full node is ~10
	///         swings and ~10 units in total — the 40 / 60 / 40 read off the earlier captures were STACK TOTALS after
	///         many swings, not one harvest's payout. Melvin narrated it as he went ("-10% each time", "also -10 %,
	///         got 1", and "got 3" after three swings on the gold vein). Correcting it means modelling depletion,
	///         which needs the health-stat broadcast too, so it is its own change — tracked in Gitea #732. Until then
	///         <see cref="DefaultYield" /> stays as-is rather than leaving a half-applied model.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5]
	///     </para>
	///     <para>
	///         The node NAME is the key deliberately, matching how shops and conversations key on the NPC name: it means
	///         no new column, no importer change and no re-harvest of <c>faolan.db</c> to turn harvesting on for the 141
	///         nodes already imported into <c>WorldObjects</c>.
	///         ⚠️ That makes the NAME load-bearing, and a <c>WorldObjects.Name</c> taken from the dynel record's own
	///         name string can be WRONG: retail ships "Silver Vein" as <i>Martial Ring</i> in that field and "Copper
	///         Node" as <i>Vitalic Bronze-studded Armbands</i>. The DISPLAYED name is the record's <c>DisplayId</c>
	///         resolved through LDB category 50200; one shipped row already carries the leaked internal name instead,
	///         which silently makes it un-harvestable.
	///     </para>
	/// </remarks>
	public static class HarvestCatalog
	{
		/// <summary>Max health every harvest node carries, and what identifies one as harvestable on the wire.</summary>
		public const uint NodeMaxHealth = 1000;

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

		/// <summary>Node display name -> the resource <c>Items.Id</c> it yields.</summary>
		public static readonly ImmutableDictionary<string, uint> Resources = new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
		{
			// Wood
			["Oak Tree"] = 161213, // Oak       - measured
			["Yew Tree"] = 161218, // Yew
			["Ash Tree"] = 161225, // Ash
			// ⚠️ Birch sits at 3927910, OUTSIDE the 1611xx band every other resource is in - it was harvested from a
			// different capture. Included because it is structurally the same kind of row (Value 0, no description,
			// hashed, name matches the node exactly), but it is the one entry whose id space does not corroborate it.
			["Birch Tree"] = 3927910, // Birch
			// Stone
			["Granite Rock"] = 161206, // Granite   - measured
			["Basalt Rock"] = 161205, // Basalt
			// Metal
			["Iron Node"] = 161196, // Iron      - measured
			["Copper Node"] = 161197, // Copper
			["Duskmetal Node"] = 161195, // Duskmetal
			["Gold Vein"] = 161200, // Gold
			["Electrum Vein"] = 161201, // Electrum
			["Silver Vein"] = 161202, // Silver
			// Cloth
			["Cotton Shrub"] = 161192 // Cotton
			// ⚠️ ABSENT ON PURPOSE: "Elm Tree" and "Slate Rock" ship as nodes but NO item named Elm or Slate exists in
			// faolan.db, so there is nothing to grant. Inventing an id would hand out the wrong resource; leaving them
			// out makes them inert and says so in the log. Tracked as a content gap - see HarvestCatalogTests.
		}.ToImmutableDictionary(System.StringComparer.OrdinalIgnoreCase);

		/// <summary>
		///     Node display name -> a measured stack size, for any node whose count differs from
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
		public static readonly ImmutableDictionary<string, uint> Yields = ImmutableDictionary.Create<string, uint>(System.StringComparer.OrdinalIgnoreCase);

		/// <summary>The resource a node yields, or null when the object is not a harvest node at all.</summary>
		public static uint? ResourceFor(string? nodeName) =>
			nodeName != null && Resources.TryGetValue(nodeName, out var itemId) ? itemId : null;

		/// <summary>How much <paramref name="nodeName" /> yields per harvest.</summary>
		public static uint YieldFor(string? nodeName) =>
			nodeName != null && Yields.TryGetValue(nodeName, out var count) ? count : DefaultYield;

		/// <summary>Whether this world object is a harvestable resource node.</summary>
		public static bool IsHarvestable(string? nodeName) => ResourceFor(nodeName) != null;
	}
}
