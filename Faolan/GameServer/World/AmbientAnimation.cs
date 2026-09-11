using System;
using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     One ambient idle animation a live creature can perform - the runtime form of an
	///     <c>NpcAmbientAnimation</c> row.
	/// </summary>
	/// <param name="DurationCentiseconds">
	///     The animation's own length, which the client is told (<c>activation_time</c>) and which also times the next
	///     one.
	/// </param>
	/// <param name="Weight">How often retail was captured playing it.</param>
	public readonly record struct AmbientAnimation(uint AnimationId, uint DurationCentiseconds, uint Weight);

	/// <summary>
	///     Picking one ambient animation out of a creature's set.
	/// </summary>
	/// <remarks>
	///     Kept beside the type rather than on <see cref="WorldEntity" /> so the choice is testable on its own: what it
	///     has to get right is the weighting, and a weighted draw that degenerates to "always the first one" looks
	///     exactly like a working one from the outside.
	/// </remarks>
	public static class AmbientAnimations
	{
		/// <summary>
		///     A weighted pick: an animation retail played twice as often is twice as likely.
		///     <paramref name="random" /> is passed in so a test can drive the draw.
		/// </summary>
		/// <returns>
		///     Null for an empty set - a creature no capture caught idling - which is the caller's cue to leave it
		///     standing still rather than invent an animation for it.
		/// </returns>
		public static AmbientAnimation? Pick(IReadOnlyList<AmbientAnimation> animations, Random random)
		{
			ArgumentNullException.ThrowIfNull(animations);
			ArgumentNullException.ThrowIfNull(random);

			if (animations.Count == 0)
				return null;

			var total = 0L;
			foreach (var a in animations)
				total += Math.Max(1u, a.Weight); // a zero-weight row is still a real animation, just an unmeasured one

			var roll = random.NextInt64(total);
			foreach (var a in animations)
			{
				roll -= Math.Max(1u, a.Weight);
				if (roll < 0)
					return a;
			}

			return animations[^1];
		}
	}
}
