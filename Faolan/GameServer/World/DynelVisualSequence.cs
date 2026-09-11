using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     One step of the staged visual sequence a harvested world object plays.
	/// </summary>
	/// <remarks>
	///     Retail does not flip a harvested node in one packet: it runs a fade-and-swap, where the object fades out,
	///     its mesh and collision are cleared while it is invisible, and it fades back in as nothing - then ten seconds
	///     later the same sequence runs backwards and the node is there again. The steps below are those moments, in
	///     order, measured off <c>xxx_mage-necromancer_login_play2_tortage_city_first-quests</c> on the three quarry
	///     Stone Bricks it harvests (handles 0x6AF3, 0x6AF9 and 0x6AFA: three harvests and two full respawns between
	///     18:43:16 and 18:44:12).
	///     <para>
	///         The second run is the node coming back rather than a second harvest. Nothing precedes it - a harvest is
	///         four BehaviorControlCenter records from the client, and there are none before the 18:43:30 sequence on
	///         0x6AF3 - so the server started it on its own; it is also the run that sets a mesh and a collision rather
	///         than clearing them, and the id it sets is the brick's own.
	///     </para>
	/// </remarks>
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

	/// <summary>A step that has come due on a zone's schedule: what to play, and on which object.</summary>
	public readonly record struct DueDynelVisualStep(Zone Zone, LiveWorldObject Object, DynelVisualStep Step);

	/// <summary>
	///     A world object's pending sequence: the steps it has left, and how long until the next one.
	/// </summary>
	/// <remarks>
	///     The delays are retail's, to the tick: 2.0 s of fade before the mesh swap, 2.0 s more before the collision
	///     goes, then 10.0 s of absence before the regrowth starts (the three harvests measure 2.05 / 2.05 / 10.24,
	///     1.88 / 2.12 / 9.92 and 2.38 / 1.66 / 10.02 seconds). They are delays rather than absolute times because the
	///     client is running a fade of exactly the same length: the swap has to land when the fade ends, not when a
	///     clock says so.
	/// </remarks>
	internal sealed class DynelVisualSequence
	{
		/// <summary>
		///     How long each fade the sequence asks for lasts. Retail sends 2.0 for every fade in the harvest sequence,
		///     and 0.2 or 0.5 elsewhere, so this is the harvest's value rather than the message's.
		/// </summary>
		public const float FadeSeconds = 2.0f;

		/// <summary>
		///     The gap between the node going away and its regrowth starting, measured from the collision clear.
		/// </summary>
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

		/// <summary>True once every step has been handed out, so the sequence can be dropped.</summary>
		public bool IsFinished => _next >= Script.Length;

		public DynelVisualSequence(LiveWorldObject obj)
		{
			Object = obj;
			DueIn = Script[0].AfterSeconds;
		}

		/// <summary>
		///     Advances by <paramref name="deltaSeconds" /> and yields every step that came due in it.
		/// </summary>
		/// <remarks>
		///     More than one step can come due: a tick longer than a step's delay must not swallow the steps it passed,
		///     or a node stops half-way through its sequence - invisible, or without collision - until the map reloads.
		/// </remarks>
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
