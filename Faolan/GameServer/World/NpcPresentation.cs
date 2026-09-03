using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.GameServer.World
{
	// What an NPC SHOWS the players around it that is not combat: its chatter and greeting lines, the ambient idle
	// animations and their timer, and the encounter bark/lure state. A component of WorldEntity (Gitea #928 item 10);
	// an entity with nothing to show holds an empty one.
	public sealed class NpcPresentation
	{
		// #766: seconds until this NPC's next ambient idle animation - the previous animation's own length plus the
		// pause it then stands for. Zero on a fresh entity, so the first one is due immediately.
		private double _ambientAnimationRemaining;

		// #502 scripted encounter. On a LURE: the boss type it swaps into, plus the two measured radii. On the BOSS:
		// EncounterLureTemplateId names the lure to restore once the boss is gone. An entity carries one side or the
		// other, never both - the swap replaces the entity rather than re-templating it, which is what the capture
		// shows (different handles in the same second).
		public uint? EncounterBossTemplateId { get; set; }

		public float EncounterBarkRadius { get; set; }

		public float EncounterTriggerRadius { get; set; }

		// Set once the lure has cried for help, so the bark fires on the APPROACH rather than every tick the player
		// stands inside the radius.
		public bool EncounterBarked { get; set; }

		// Stamp this entity as the LURE of `encounter` (a no-op when it has none, which is the common case - most NPCs
		// are not part of a scripted encounter). Both the zone load and the lure RESTORE arm an entity the same way,
		// which is why it lives on the entity rather than on either caller.
		public void ArmEncounter(NpcEncounter? encounter)
		{
			if (encounter == null)
				return;

			EncounterBossTemplateId = encounter.BossNpcTemplateId;
			EncounterBarkRadius = encounter.BarkRadius;
			EncounterTriggerRadius = encounter.TriggerRadius;
			EncounterBarked = false;
		}

		// On the boss: the lure type to put back, and where. Position comes from the boss itself - it spawned on the
		// lure's exact spot, so restoring means spawning the lure type where the boss stood.
		public uint? EncounterLureTemplateId { get; set; }

		// True once the reset has been emitted, so a boss corpse cannot restore its lure twice.
		public bool EncounterResetDone { get; set; }

		// AMBIENT bark lines (verbatim <remoteformat> XML) this NPC type periodically local-says, from NpcChatter at
		// zone load. Null/empty = this NPC has no ambient chatter group and stays silent.
		//
		// ⚠️ This is only the `conversation1`/`conversation2` tier. A bark's CUE is a property of the bark, not of the
		// world tick (#694): the crier's `sendtell` greeting and the Damsel's `event` cry live in GreetingLines and
		// EncounterLines respectively, because putting all three on one clock made the crier greet an empty street and
		// double-fired the Damsel alongside her own encounter trigger. `NpcChatterBarks.TriggerOf` is the split.
		public NpcChatterBark[] ChatterLines { get; set; } = null!;

		// DETECTION barks (`sendtell`): said once TO a player the NPC notices arriving, not on a clock (#694). The
		// channel names the delivery - a tell has one recipient - and the line's `DetectedChar.name` slot names them.
		public NpcChatterBark[] GreetingLines { get; set; } = null!;

		// The AMBIENT IDLE ANIMATIONS this NPC's creature performs while standing about (#766), from
		// NpcTemplate.AmbientAnimations at zone load - weighted, because a creature's common idle is common. Empty =
		// no capture ever caught this creature idling, so it stands still rather than performing a guessed animation.
		public IReadOnlyList<AmbientAnimation> AmbientAnimations { get; set; } = [];

		// The animation the tick most recently PICKED, for the broadcaster to send. Set by the tick alongside the
		// NpcAmbientAnimation effect, so the choice (and its stated duration) is made once rather than re-rolled on the
		// wire side, where the two would disagree about what the client was told.
		public AmbientAnimation? AmbientAnimationPicked { get; set; }

		// ENCOUNTER-SCRIPT barks (`event`): fired by the scripted encounter's own proximity trigger (#502), never by
		// the ambient tick. Kept apart from ChatterLines so the lure's cry for help cannot also come out on the clock.
		public NpcChatterBark[] EncounterLines { get; set; } = null!;

		// The players this NPC has already greeted, so a detection bark is said once per arrival rather than every
		// tick the player stands in range (#694). A player is forgotten again once they leave `forgetRange` - see
		// WorldState.GreetingForgetRange - so walking away and coming back is a fresh arrival, which is what a
		// greeting means, while standing on the boundary cannot re-trigger it.
		public HashSet<uint> GreetedPlayers { get; } = [];

		// #766: counts down to this NPC's next ambient idle animation; true on the tick one is due. `Arm` schedules the
		// next, which is the animation's own length plus the pause the creature then stands for - retail never starts
		// one before the last has finished (see BehaviorAnimations.AmbientEmote).
		public bool TickAmbientAnimation(double dt)
		{
			if (AmbientAnimations.Count == 0)
				return false;

			_ambientAnimationRemaining -= dt;
			return _ambientAnimationRemaining <= 0;
		}

		public void ArmAmbientAnimation(double seconds)
		{
			_ambientAnimationRemaining = seconds;
		}
	}
}
