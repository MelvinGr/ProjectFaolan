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
	///         ⚠️ The <b>yield</b> is the soft part. Only three counts are measured (40 / 60 / 40) and they do not
	///         correlate with anything else observed, so <see cref="DefaultYield" /> carries the modal 40 for every
	///         unmeasured node and <see cref="Yields" /> holds only what a capture actually showed. A count is cheap to
	///         confirm — one more harvest of any node type fills a row — so this is written to be extended, not
	///         interpolated.
	///     </para>
	///     <para>
	///         The node NAME is the key deliberately, matching how shops and conversations key on the NPC name: it means
	///         no new column, no importer change and no re-harvest of <c>faolan.db</c> to turn harvesting on for the 141
	///         nodes already imported into <c>WorldObjects</c>.
	///     </para>
	/// </remarks>
	public static class HarvestCatalog
	{
		/// <summary>Max health every harvest node carries, and what identifies one as harvestable on the wire.</summary>
		public const uint NodeMaxHealth = 1000;

		/// <summary>
		///     A node's health bottoms out at <b>1</b>, never 0 — measured across every capture — and the node then
		///     despawns. So "exhausted" is <c>health &lt;= ExhaustedHealth</c>, not <c>== 0</c>.
		/// </summary>
		public const uint ExhaustedHealth = 1;

		/// <summary>The yield used for a node whose count no capture has measured. The modal observed value.</summary>
		public const uint DefaultYield = 40;

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
		///     Node display name -> the stack size it yields, for the nodes a capture measured. Absent = use
		///     <see cref="DefaultYield" />.
		/// </summary>
		public static readonly ImmutableDictionary<string, uint> Yields = new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase)
		{
			["Granite Rock"] = 40,
			["Oak Tree"] = 60,
			["Iron Node"] = 40
		}.ToImmutableDictionary(System.StringComparer.OrdinalIgnoreCase);

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
