using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	// One step of the staged visual sequence a harvested world object plays (#678).
	//
	// Retail does NOT flip a harvested node in one packet. It runs a fade-and-swap: the object fades out, its mesh and
	// collision are cleared while it is invisible, it fades back in as nothing - and ten seconds later the same
	// sequence runs backwards and the node is there again. The steps below are those moments, in order, measured off
	// `xxx_mage-necromancer_login_play2_tortage_city_first-quests` on the three quarry Stone Bricks it harvests
	// (handles 0x6AF3 / 0x6AF9 / 0x6AFA, three harvests and two full respawns between 18:43:16 and 18:44:12).
	//
	// ⚠️ **The second run is the node coming BACK, not a second harvest**, which is what #529 and the old
	// `MarkPickupHarvested` comment read it as. Nothing precedes it: the harvest itself is four
	// BehaviorControlCenter records from the CLIENT, and there are none before the 18:43:30 sequence on 0x6AF3 - the
	// server started it on its own. It is also the run that sets a mesh and a collision rather than clearing them,
	// and the id it sets is the brick's own (`quest_quarry_stonebrick01`), so it can only be the regrowth.
	public enum DynelVisualStep
	{
		// The fade-out has finished: draw nothing, and fade the (now empty) object back in.
		HarvestClearMesh,

		// The fade-in has finished: the node is visibly gone, so drop its collision and restate its CarsGroup.
		HarvestClearCollision,

		// The respawn delay elapsed: fade out again, this time to bring the node back.
		RespawnFadeOut,

		// Put the object's own mesh back and fade it in.
		RespawnMesh,

		// Restore its collision and restate its CarsGroup - the node is harvestable again.
		RespawnCollision
	}

	// A step that has come due on a zone's schedule: what to play, and on which object.
	public readonly record struct DueDynelVisualStep(Zone Zone, LiveWorldObject Object, DynelVisualStep Step);

	// A world object's pending sequence: the steps it has left, and how long until the next one.
	//
	// The delays are retail's, to the tick: 2.0 s of fade before the mesh swap, 2.0 s more before the collision goes,
	// then 10.0 s of absence before the regrowth starts (the three harvests measure 2.05 / 2.05 / 10.24, 1.88 / 2.12
	// / 9.92 and 2.38 / 1.66 / 10.02 seconds). They are delays rather than absolute times because the CLIENT is
	// running a fade of exactly the same length - the swap has to land when the fade ends, not when a clock says so.
	internal sealed class DynelVisualSequence
	{
		// How long each fade the sequence asks for lasts. Retail sends 2.0 for every fade in the harvest sequence
		// (0x40000000); it sends 0.2 and 0.5 elsewhere, so this is the harvest's value, not the message's.
		public const float FadeSeconds = 2.0f;

		// The gap between the node going away and its regrowth starting, measured from the collision clear.
		public const double RespawnDelaySeconds = 10.0;

		private static readonly (DynelVisualStep Step, double AfterSeconds)[] Script =
		[
			(DynelVisualStep.HarvestClearMesh, FadeSeconds),
			(DynelVisualStep.HarvestClearCollision, FadeSeconds),
			(DynelVisualStep.RespawnFadeOut, RespawnDelaySeconds),
			(DynelVisualStep.RespawnMesh, FadeSeconds),
			(DynelVisualStep.RespawnCollision, FadeSeconds)
		];

		private int _next;

		public LiveWorldObject Object { get; }

		public double DueIn { get; private set; }

		// True once every step has been handed out - the sequence can be dropped.
		public bool IsFinished => _next >= Script.Length;

		public DynelVisualSequence(LiveWorldObject obj)
		{
			Object = obj;
			DueIn = Script[0].AfterSeconds;
		}

		// Advances by `deltaSeconds` and yields every step that came due in it. More than one can: a tick longer than
		// a step's delay must not silently swallow the steps it passed, or a node would stop half-way through its
		// sequence - invisible, or without collision, until the map reloads.
		public IEnumerable<DynelVisualStep> Advance(double deltaSeconds)
		{
			DueIn -= deltaSeconds;
			while (!IsFinished && DueIn <= 0)
			{
				var step = Script[_next++];
				if (!IsFinished)
					DueIn += Script[_next].AfterSeconds;

				yield return step.Step;
			}
		}
	}
}
