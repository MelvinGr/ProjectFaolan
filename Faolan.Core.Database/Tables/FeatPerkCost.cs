using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     What one alternate-advancement perk rank costs, and in which AA point pool - RDB type <b>1000099</b>
	///     ("feat point cost info"), 3,096 records of twelve bytes.
	/// </summary>
	/// <remarks>
	///     <b>The pool is the client's own <c>AAFeatPlannerPointType</c> enum.</b> The record's middle word is read
	///     at <c>+4</c> by <c>GetPointCost</c> (<c>FUN_01506700</c>, the <c>FeatWindows.AAFeatTree</c> GameInterface
	///     API), which switches on it into the three GUI keys <i>PvPPoints</i> / <i>PvEPoints</i> /
	///     <i>GeneralPoints</i> for the values <b>2 / 3 / 4</b> - the same three ids
	///     <see cref="TokenPool.AltAdvancementPvPToken" /> / <see cref="TokenPool.AltAdvancementPvEToken" /> /
	///     <see cref="TokenPool.AltAdvancementGeneralToken" /> carry in the client's token table (RDB 1000520), and
	///     the same enum the planner registers as <c>e_PvP = 2</c>, <c>e_PvE = 3</c>, <c>e_Generic = 4</c>.
	///     <para>
	///         The shipped table uses only the two specific pools - 1,548 rows each - and never the generic one.
	///         2,664 perk ranks are priced in exactly one of them and 216 in both, at the same price; the split is
	///         semantic rather than arbitrary, with the crowd-control resistances (<c>Root</c> / <c>Snare</c> /
	///         <c>Stun</c> / <c>Fear</c> / <c>Stagger Resistance</c>, <c>Quick Steps</c>) priced in PvP points and the
	///         elemental wards (<c>Fire</c> / <c>Frost</c> / <c>Electric</c> / <c>Unholy</c> / <c>Holy Wards</c>) in
	///         PvE ones.
	///     </para>
	///     <para>
	///         <see cref="FeatClassEntryId" /> is a <see cref="FeatClassEntry" /> row id, not a feat id - so a cost
	///         is attached to a perk's placement in one class's tree, not to the perk. All 3,096 rows resolve, and
	///         every one of them lands in the twelve AA trees (<c>TreeId</c> 1000177-1000190); no ordinary feat has a
	///         cost row, because an ordinary feat costs one point per rank (see <c>FeatRules.PointCost</c>).
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(FeatClassEntryId), nameof(PointType))]
	public class FeatPerkCost
	{
		// The featclass row this price is for - FeatClassEntry.Id, the client's own row id.
		public uint FeatClassEntryId { get; set; }

		[ForeignKey(nameof(FeatClassEntryId))]
		public virtual FeatClassEntry Entry { get; set; } = null!;

		// Which AA point pool the price is denominated in - the record's word 1.
		[ConstrainToEnumValues]
		public TokenPool PointType { get; set; }

		// The price in that pool - the record's word 2. 1..50 across the shipped table, rising with the rank
		// (`Electric Wards` runs 1 / 4 / 8 / 14 / 23 over its five ranks).
		public uint Cost { get; set; }
	}
}
