using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.World
{
	// Which of `WorldObjectCodec`'s dynel classes are PLACED world objects.
	//
	// NOT a codec - a policy statement about the family, the shape `LootContainerTemplate` and `PatrolRoute` set for
	// what sits beside a codec without encoding anything. It lives here because #530 rule 7 puts the IIR NAMES inside
	// `Codecs/`: the importer's world-object harvest used to spell five of them itself, which is the same invisibility
	// that let duplicate emitters accumulate.
	//
	// `HandledTypes` is one wider than this, deliberately: a TombStone is a dead player's marker, so it parses as a
	// dynel but is never baked into the WorldObjects table.
	public static class WorldObjectClasses
	{
		// The plain decor/prop dynel - the class a caller names when it means "an ordinary world object" rather than
		// the whole family (the corpus extract's world-object slice, the burst's column lift).
		public const uint SimpleDynel = (uint)IirTypes.SimpleDynelFullUpdateIIR;

		public static readonly IReadOnlySet<uint> Placed = new HashSet<uint>
		{
			(uint)IirTypes.SimpleDynelFullUpdateIIR, (uint)IirTypes.LootBagFullUpdateIIR,
			(uint)IirTypes.DoorFullUpdateIIR, (uint)IirTypes.DestructibleFullUpdateIIR,
			(uint)IirTypes.ClimbingDynelFullUpdateIIR
		};

		// The classes that can back a zone-line DOOR: the dedicated door class, plus SimpleDynel - which also backs
		// same-playfield doors like the Thirsty Dog Inn's. A candidate only becomes a teleport link when a teleport
		// follows its use, so the broader class costs nothing (decor never teleports anyone).
		public static readonly IReadOnlySet<uint> Doors = new HashSet<uint>
		{
			(uint)IirTypes.DoorFullUpdateIIR, (uint)IirTypes.SimpleDynelFullUpdateIIR
		};
	}
}
