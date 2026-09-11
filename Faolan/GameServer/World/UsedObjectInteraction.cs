using System.Numerics;
using Faolan.Core.Enums;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The column-driven interaction of a used world object: its <c>InteractAction</c> - what the object does -
	///     plus, for a teleporter, the resolved destination. It lets the use handler switch on the action rather than
	///     special-casing one kind.
	/// </summary>
	/// <param name="Action">
	///     None when the handle is not a database world object at all - a loot bag or a zone entity - and the caller
	///     falls through to the open-animation or loot path.
	/// </param>
	/// <param name="WorldObjectId">
	///     The used object's stable row id, null when the handle resolved to nothing, so a caller can look the object
	///     up. The handle itself is session-volatile and useless as a key.
	/// </param>
	/// <remarks>
	///     Returned by <see cref="WorldObjectService.ResolveUseObject" />, and beside
	///     <see cref="ZoneLineDestination" /> because neither is meaningful without the other: the destination only
	///     arrives inside one of these.
	/// </remarks>
	public readonly record struct UsedObjectInteraction(InteractAction Action, ZoneLineDestination Teleport, uint? WorldObjectId = null);

	/// <summary>
	///     The resolved destination of a zone-line teleporter: the map to hand the player off to, and where to place
	///     them on it.
	/// </summary>
	/// <param name="DestMapId">
	///     0 means the object is a teleporter whose destination has not been captured: the caller surfaces a message
	///     rather than silently dropping the player somewhere.
	/// </param>
	/// <param name="AcgPlayfieldClass">
	///     Non-zero when this destination is entered through the ACG instance bind box rather than by teleporting
	///     straight through. Harvested per object, because nothing about the destination map distinguishes it - see
	///     <c>WorldObject.DestinationPlayfieldClass</c>.
	/// </param>
	public readonly record struct ZoneLineDestination(uint DestMapId, Vector3? ArrivalPosition, string DestinationName, uint AcgPlayfieldClass = 0);
}
