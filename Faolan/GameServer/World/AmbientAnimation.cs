using System;
using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	// One ambient idle animation a live creature can perform (#766) - the runtime form of an `NpcAmbientAnimation` row.
	// `DurationCentiseconds` is the animation's own length, which the client is told (`activation_time`) and which also
	// times the next one, and `Weight` is how often retail was captured playing it.
	public readonly record struct AmbientAnimation(uint AnimationId, uint DurationCentiseconds, uint Weight);

	// Picking one ambient animation out of a creature's set. Kept beside the type rather than on WorldEntity so the
	// choice is testable on its own - what it has to get right is the WEIGHTING, and a weighted draw that quietly
	// degenerates to "always the first one" looks exactly like a working one from the outside.
	public static class AmbientAnimations
	{
		// A weighted pick: an animation retail played twice as often is twice as likely. `random` is passed in so a test
		// can drive the draw. Returns null for an empty set (a creature no capture caught idling), which is the caller's
		// cue to leave it standing still rather than invent an animation for it.
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
