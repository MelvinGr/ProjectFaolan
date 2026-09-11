using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     Named reads out of a <see cref="BehaviorControlCenterIIR" /> tail.
	/// </summary>
	/// <remarks>
	///     Not a codec, and deliberately not a decode: the tail is four different shapes discriminated by the behaviour id
	///     (an echoed client tail, an equip identity block, the 81-byte hit-result block, or a per-behaviour literal), so the
	///     codec carries it verbatim. The handful of words the server needs out of it are named here - an offset into a
	///     message body belongs beside the codec that owns the body, never at the call site. Compare
	///     <see cref="World.PlayfieldTokenScan" /> and <see cref="CastGroundTargetScan" />.
	/// </remarks>
	public static class BehaviorTailScan
	{
		/// <summary>
		///     The ability id of an unarmed swing: the last word of the request's tail. Zero when the tail is too short to
		///     carry one (the air-swing forms).
		/// </summary>
		public static uint AbilityId(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= sizeof(uint)
				? tail.ReadU32Be(tail.Length - sizeof(uint))
				: 0;
		}

		/// <summary>
		///     The tradeskill request carried by a 0x27 tail.
		/// </summary>
		/// <remarks>
		///     Unlike everything else here this is a real decode rather than a named read - the tail's leading byte
		///     discriminates a craft from a socketing and each has its own payload - so the grammar lives in
		///     <see cref="TradeskillCommandCodec" /> and this is only the entry point, keeping every tail read on one seam.
		/// </remarks>
		public static TradeskillCommand Tradeskill(ReadOnlySpan<byte> tail)
		{
			return TradeskillCommandCodec.Parse(tail);
		}

		/// <summary>
		///     The moved item's template handle on an equip/unequip interact: the first word of the trailing struct that
		///     follows the action's from/target pair. Zero when the tail carries none (an unequip to an empty slot).
		/// </summary>
		public static uint MovedItemHandle(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= sizeof(uint)
				? tail.ReadU32Be(0)
				: 0;
		}

		/// <summary>
		///     The entity a UseDynel (behaviour id 0x12) acted on: the second word of the tail's leading [from][target] pair,
		///     <c>from</c> being the object's typetag. Zero when the tail is too short.
		/// </summary>
		/// <remarks>
		///     This is not the action's own target: on a UseObject that field holds the player's own instance and the object
		///     appears only in the tail. A TalkToNpc is the other way round - its NPC handle is the action target - so the two
		///     interacts cannot share a read.
		/// </remarks>
		public static uint InteractTargetHandle(ReadOnlySpan<byte> tail)
		{
			return tail.Length >= 2 * sizeof(uint)
				? tail.ReadU32Be(sizeof(uint))
				: 0;
		}
	}
}
