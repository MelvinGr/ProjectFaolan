using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     What an NPC shows the players around it that is not combat: its chatter and greeting lines, the ambient idle
	///     animations and their timer, and the encounter bark and lure state.
	/// </summary>
	/// <remarks>A component of <see cref="WorldEntity" />; an entity with nothing to show holds an empty one.</remarks>
	public sealed class NpcPresentation
	{
		// Seconds until this NPC's next ambient idle animation - the previous animation's own length plus the pause it
		// then stands for. Zero on a fresh entity, so the first one is due immediately.
		private double _ambientAnimationRemaining;

		/// <summary>
		///     On a lure: the boss type it swaps into, alongside the two measured radii. On the boss,
		///     <see cref="EncounterLureTemplateId" /> names the lure to restore once it is gone.
		/// </summary>
		/// <remarks>
		///     An entity carries one side or the other, never both: the swap replaces the entity rather than
		///     re-templating it, which is what the capture shows - different handles in the same second.
		/// </remarks>
		public uint? EncounterBossTemplateId { get; set; }

		public float EncounterBarkRadius { get; set; }

		public float EncounterTriggerRadius { get; set; }

		// Set once the lure has cried for help, so the bark fires on the approach rather than every tick the player
		// stands inside the radius.
		public bool EncounterBarked { get; set; }

		/// <summary>
		///     Stamps this entity as the lure of <paramref name="encounter" />, and a no-op when it has none, which is
		///     the common case.
		/// </summary>
		/// <remarks>
		///     Both the zone load and the lure restore arm an entity the same way, which is why this lives on the
		///     entity rather than on either caller.
		/// </remarks>
		public void ArmEncounter(NpcEncounter? encounter)
		{
			if (encounter == null)
				return;

			EncounterBossTemplateId = encounter.BossNpcTemplateId;
			EncounterBarkRadius = encounter.BarkRadius;
			EncounterTriggerRadius = encounter.TriggerRadius;
			EncounterBarked = false;
		}

		/// <summary>
		///     On the boss: the lure type to put back. The position comes from the boss itself, which spawned on the
		///     lure's exact spot, so restoring means spawning the lure type where the boss stood.
		/// </summary>
		public uint? EncounterLureTemplateId { get; set; }

		/// <summary>True once the reset has been emitted, so a boss corpse cannot restore its lure twice.</summary>
		public bool EncounterResetDone { get; set; }

		/// <summary>
		///     Ambient bark lines - verbatim remoteformat XML - this NPC type periodically local-says, from
		///     <c>NpcChatter</c> at zone load. Null or empty means it has no ambient chatter group and stays silent.
		/// </summary>
		/// <remarks>
		///     Only the <c>conversation1</c> and <c>conversation2</c> tier. A bark's cue is a property of the bark
		///     rather than of the world tick: a <c>sendtell</c> greeting lives in <see cref="GreetingLines" /> and an
		///     <c>event</c> cry in <see cref="EncounterLines" />, because putting all three on one clock makes a crier
		///     greet an empty street and double-fires an encounter line alongside its own trigger.
		///     <c>NpcChatterBarks.TriggerOf</c> is the split.
		/// </remarks>
		public NpcChatterBark[] ChatterLines { get; set; } = null!;

		/// <summary>
		///     Detection barks (<c>sendtell</c>): said once to a player the NPC notices arriving, not on a clock. The
		///     channel names the delivery - a tell has one recipient - and the line's <c>DetectedChar.name</c> slot
		///     names them.
		/// </summary>
		public NpcChatterBark[] GreetingLines { get; set; } = null!;

		/// <summary>
		///     The ambient idle animations this NPC's creature performs while standing about, from
		///     <c>NpcTemplate.AmbientAnimations</c> at zone load - weighted, because a creature's common idle is
		///     common. Empty means no capture caught this creature idling, so it stands still rather than performing a
		///     guessed animation.
		/// </summary>
		public IReadOnlyList<AmbientAnimation> AmbientAnimations { get; set; } = [];

		/// <summary>
		///     The animation the tick most recently picked, for the broadcaster to send. Set by the tick alongside the
		///     NpcAmbientAnimation effect, so the choice and its stated duration are made once rather than re-rolled on
		///     the wire side, where the two would disagree about what the client was told.
		/// </summary>
		public AmbientAnimation? AmbientAnimationPicked { get; set; }

		/// <summary>
		///     Encounter-script barks (<c>event</c>): fired by the scripted encounter's own proximity trigger, never by
		///     the ambient tick. Kept apart from <see cref="ChatterLines" /> so a lure's cry for help cannot also come
		///     out on the clock.
		/// </summary>
		public NpcChatterBark[] EncounterLines { get; set; } = null!;

		/// <summary>
		///     The players this NPC has already greeted, so a detection bark is said once per arrival rather than every
		///     tick the player stands in range.
		/// </summary>
		/// <remarks>
		///     A player is forgotten once they leave the forget range - see <c>WorldState.GreetingForgetRange</c> - so
		///     walking away and coming back is a fresh arrival, which is what a greeting means, while standing on the
		///     boundary cannot re-trigger it.
		/// </remarks>
		public HashSet<uint> GreetedPlayers { get; } = [];

		/// <summary>
		///     Counts down to this NPC's next ambient idle animation; true on the tick one is due.
		/// </summary>
		/// <remarks>
		///     <see cref="ArmAmbientAnimation" /> schedules the next, which is the animation's own length plus the
		///     pause the creature then stands for: retail never starts one before the last has finished.
		/// </remarks>
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
