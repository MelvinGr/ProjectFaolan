using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Named reads out of a `BehaviorControlCenterIIR.Tail`.
	//
	// NOT a codec, and deliberately not a decode: the tail is genuinely four different shapes discriminated by
	// `behaviorId` (an echoed client tail, an equip identity block, the 132-byte hit-result block, or a per-behaviour
	// literal) and stays an opaque region until #536 settles it. What the server needs from it today is two single
	// words, and this is where they are NAMED - the shape `PlayfieldTokenScan` and `CastGroundTargetScan` set for
	// reading a field out of a region the codec carries verbatim.
	//
	// Both used to be fixed offsets in `Faolan/` (`actionTrailer.ReadU32Be(0)` and a slice at `26 + <u32 at 22>`),
	// which is what #530 rule 7 evicts: an offset into a message body belongs beside the codec that owns the body.
	public static class BehaviorTailScan
	{
		// The ABILITY id of an unarmed swing: the last word of the request's tail. 0 when the tail is too short to
		// carry one (the air-swing forms).
		public static uint AbilityId(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= sizeof(uint)
				? tail.ReadU32Be(tail.Length - sizeof(uint))
				: 0;
		}

		// The moved item's TEMPLATE handle on an equip/unequip interact: the first word of the trailing struct that
		// follows the action's from/target pair. 0 when the tail carries none (an unequip to an empty slot).
		public static uint MovedItemHandle(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= sizeof(uint)
				? tail.ReadU32Be(0)
				: 0;
		}

		// The entity a UseObject (ActionType 0x12) acted on: the SECOND word of the tail's leading `[from][target]`
		// pair, `from` being the object's typetag. 0 when the tail is too short.
		//
		// ⚠️ NOT `args.Target` - on a UseObject that holds the PLAYER's own instance, and the object is only in the
		// tail. A TalkToNpc is the other way round (its NPC handle IS `args.Target.Instance`), so the two interacts
		// cannot share a read. Both importer readers of this word had the offset spelled out by hand.
		public static uint InteractTargetHandle(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= 2 * sizeof(uint)
				? tail.ReadU32Be(sizeof(uint))
				: 0;
		}
	}
}
