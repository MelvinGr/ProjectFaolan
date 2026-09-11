using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>Which of <see cref="WorldObjectCodec" />'s dynel classes are placed world objects.</summary>
	/// <remarks>
	///     Not a codec but a policy statement about the family, in the shape `LootContainerTemplate` and
	///     <see cref="PatrolRoute" /> set for what sits beside a codec without encoding anything. It lives here so that
	///     the IIR names stay inside `Codecs/` rather than being spelled out by each consumer.
	///     <para>
	///         <see cref="WorldObjectCodec.HandledTypes" /> is deliberately one wider than this: a TombStone is a dead
	///         player's marker, so it parses as a dynel but is never baked into the WorldObjects table.
	///     </para>
	/// </remarks>
	public static class WorldObjectClasses
	{
		/// <summary>
		///     The plain decor/prop dynel - the class a caller names when it means "an ordinary world object" rather than
		///     the whole family.
		/// </summary>
		public const uint SimpleDynel = (uint)IirTypes.SimpleDynelFullUpdateIIR;

		/// <summary>
		///     The dropped-loot dynel. Named for the same reason as <see cref="SimpleDynel" />: a caller that wants bags
		///     specifically would otherwise spell the IIR itself, outside Codecs/.
		/// </summary>
		public const uint LootBag = (uint)IirTypes.LootBagFullUpdateIIR;

		public static readonly IReadOnlySet<uint> Placed = new HashSet<uint>
		{
			(uint)IirTypes.SimpleDynelFullUpdateIIR, (uint)IirTypes.LootBagFullUpdateIIR,
			(uint)IirTypes.DoorFullUpdateIIR, (uint)IirTypes.DestructibleFullUpdateIIR,
			(uint)IirTypes.ClimbingDynelFullUpdateIIR
		};

		/// <summary>
		///     The classes that can back a zone-line door: the dedicated door class, plus SimpleDynel, which also backs
		///     same-playfield doors like the Thirsty Dog Inn's.
		/// </summary>
		/// <remarks>
		///     A candidate only becomes a teleport link when a teleport follows its use, so the broader class costs
		///     nothing: decor never teleports anyone.
		/// </remarks>
		public static readonly IReadOnlySet<uint> Doors = new HashSet<uint>
		{
			(uint)IirTypes.DoorFullUpdateIIR, (uint)IirTypes.SimpleDynelFullUpdateIIR
		};
	}
}
