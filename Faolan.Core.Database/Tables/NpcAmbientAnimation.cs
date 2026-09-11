using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One ambient animation an NPC type idly performs - a citizen leaning on a wall, scratching, looking round.
	// Retail streams these as `BehaviorId.Emote` records (`BehaviorAnimations.AmbientEmote`) while a creature stands
	// about, and they are what makes a street look inhabited rather than lined with statues.
	//
	// Keyed by the NPC type, like NpcChatter: the set belongs to the creature, not to a placement. Harvested from the
	// corpus by the `npc-ambient-animation` module (`--report ambient-animation` measures the same records), so an
	// un-captured type simply has none and stands still - the honest gap, rather than a guessed animation.
	//
	// Natural composite key, no surrogate: the importer delete+reinserts these rows on every re-harvest (CLAUDE.md's
	// model conventions).
	[PrimaryKey(nameof(NpcTemplateId), nameof(AnimationId))]
	public class NpcAmbientAnimation
	{
		// The NPC type that performs it (loader reads via NpcTemplate.AmbientAnimations).
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The animation, as the wire names it: the Emote record's `bcc_action_id`. The same id space the type's
		// `NpcStatCore.AnimIdleId` uses (both cluster around 41-42k and 62k).
		public uint AnimationId { get; set; }

		// How long the animation runs, in centiseconds - the record's `activation_time`. It belongs to the animation
		// rather than to the send: of the 118 ids the corpus carries, 76 state exactly one value and none states more
		// than two. The runtime tells the client this number and uses it to schedule the next animation, since retail
		// never starts one before the last has finished (p10 of gap/duration is exactly 1.0 over 22,696 pairs).
		public uint DurationCentiseconds { get; set; }

		// How many times retail was captured playing it for this type. The runtime picks weighted by this, so a
		// creature's common idle stays common instead of every animation being equally likely - `Citizen` 14522 plays
		// 62171 and 62172 about 570 times each against 182 for 62188.
		public uint Weight { get; set; }
	}
}
