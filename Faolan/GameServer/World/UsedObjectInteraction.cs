using System.Numerics;
using Faolan.Core.Enums;

namespace Faolan.GameServer.World
{
	// The column-driven interaction of a USED world object: its InteractAction (what the object does) plus, for a
	// teleporter, the resolved Teleport destination. Action is None when the handle isn't a DB world object at all
	// (e.g. a loot bag, a zone entity) - the caller falls through to the open-animation / loot path. Lets the
	// UseObject handler switch on the action rather than special-casing one kind.
	// WorldObjectId is the used object's STABLE row id (null when the handle resolved to nothing), so a caller can
	// look the object up in the DB - the handle itself is session-volatile and useless as a key (#523).
	//
	// Returned by WorldObjectService.ResolveUseObject. It sits beside ZoneLineDestination because neither is
	// meaningful without the other - the destination only ever arrives inside one of these.
	public readonly record struct UsedObjectInteraction(InteractAction Action, ZoneLineDestination Teleport, uint? WorldObjectId = null);

	// The resolved destination of a zone-line teleporter: the map to hand the player off to, and where to place
	// them on it. DestMapId == 0 means the object IS a teleporter but its destination hasn't been captured yet -
	// the caller surfaces a message instead of silently dropping the player somewhere (no fallbacks).
	// `AcgPlayfieldClass` is non-zero when this destination is entered through the ACG instance BIND box rather
	// than by teleporting straight through (#821) - harvested per object, because nothing about the destination
	// MAP distinguishes it (see WorldObject.DestinationPlayfieldClass).
	public readonly record struct ZoneLineDestination(uint DestMapId, Vector3? ArrivalPosition, string DestinationName, uint AcgPlayfieldClass = 0);
}
