using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The from-scratch post-IIR streams, as value factories rather than codec surface: they name the semantic forms a
	///     spawn is built in, and <see cref="SpawnPostIirCodec" /> stays the one encoder.
	/// </summary>
	/// <remarks>
	///     A freshly spawned no-morph NPC has the minimal form, a single 41-byte stream. Appearance-bearing NPCs instead
	///     carry a morph table - the "*key=val" texscript string followed by [u32 morphId][float32] pairs - which is stored
	///     as morph rows. Emitting the minimal form for such an NPC drops that table and the client renders it bald.
	/// </remarks>
	public static class SpawnPostIirs
	{
		/// <summary>
		///     The minimal post-IIR length, in bytes, for a freshly spawned world NPC. It is not the shortest form on the
		///     wire: the 21 create-editor preview spawns carry a 40-byte one, identical except that its first record has no
		///     move-task slot. Censused over all 11,632 captured streams.
		/// </summary>
		public const int MinimalNpcLength = 41;

		/// <summary>
		///     The three movement modes retail sends, censused over the 3,272 captured minimal forms: walk 757 times, run
		///     2,492 and sprint 23. They are the same domain as the character message's own <c>movement_mode</c> field - the
		///     two are one value sent twice, see <see cref="SpawnPostIir.MovementMode" /> - so a spawn must carry the same
		///     number in both places, and NpcSpawnBuilder derives this one from the message rather than storing it twice.
		/// </summary>
		/// <remarks>
		///     The names come from the client rather than from the capture. The mode picks a speed out of the entity's
		///     movement-set block, which is the <c>&lt;movementset&gt;</c> XML (RDB 1010205) laid out in its parser's own
		///     field order, so each mode's field triple names it outright:
		///     <code>
		///     mode | forward          | backward             | strafe
		///     1|4  | basewalkspeed    | basereversewalkspeed | strafewalkspeed
		///     2|5  | baserunspeed     | basereverserunspeed  | straferunspeed
		///     3    | basesprintspeed  | basereverserunspeed  | straferunspeed
		///     6    | chargespeed x a factor
		///     </code>
		///     Mode 3 reusing run's reverse and strafe is what pins it: the movementset has no sprint reverse or sprint
		///     strafe attribute to read. Modes 4 and 5 are an unnamed parallel pair sharing the walk and run speeds exactly,
		///     and 6 is the vehicle/charge mode; none of the three has been seen on the wire. The empirical anchor is
		///     WorldState.RouteWalkSpeed at 2.2 units per second, measured off retail's own patrollers and exactly the
		///     Default Movementset's <c>basewalkspeed</c>, on NPCs sent as mode 1.
		///     <para>
		///         These stay a uint rather than an enum on purpose: the client's switch has six arms, and a switch over a
		///         three-member enum would silently discard the rest.
		///     </para>
		/// </remarks>
		public const uint MovementModeWalk = 1;
		public const uint MovementModeRun = 2;
		public const uint MovementModeSprint = 3;

		/// <summary>
		///     The value sent when a type carries no movement mode of its own. Run is retail's modal value, on 71% of
		///     captured NPC spawns and on every captured player spawn, so the default sits inside retail's own distribution
		///     rather than being invented - the test any per-spawn-event field's default has to meet.
		/// </summary>
		public const uint DefaultMovementMode = MovementModeRun;

		/// <summary>
		///     Whether a value is one retail has ever sent in this slot. Used by the appearance classifier to tell a minimal
		///     stream from a runtime-bearing one.
		/// </summary>
		public static bool IsMovementMode(uint value)
		{
			return value is MovementModeWalk or MovementModeRun or MovementModeSprint;
		}

		/// <summary>
		///     A fresh NPC's stream: the move-task slot with no task in it - the one zero byte that makes its first record
		///     five bytes where a player's is four - no buffs, <paramref name="morphs" /> in the entry slot, and three empty
		///     closing pair lists.
		/// </summary>
		/// <remarks>
		///     The bytes after the header are not constant terminators: they are an empty buff list, an empty morph table and
		///     three empty closing pair lists, four of whose framing words are the empty-list frame. See
		///     <see cref="ListFrame" />.
		/// </remarks>
		public static SpawnPostIir Npc(uint movementMode = DefaultMovementMode, IReadOnlyList<AppearanceEntry>? morphs = null)
		{
			var slot = new SpawnEntrySlot();
			if (morphs != null)
				slot.Appearance.AddRange(morphs);

			return new SpawnPostIir
			{
				MovementMode = movementMode,
				HasMoveTaskSlot = true,
				Slot = slot
			};
		}

		/// <summary>
		///     A player's stream: no move-task slot at all, and the entry table and skill id list in the entry slot. The
		///     movement mode is run on every captured player spawn, as is the player's own <c>movement_mode</c> field on all
		///     209 of them - the cross-family confirmation that the two are one value.
		/// </summary>
		public static SpawnPostIir Player()
		{
			return new SpawnPostIir
			{
				MovementMode = PlayerMovementMode,
				HasMoveTaskSlot = false,
				Slot = new SpawnEntrySlot()
			};
		}

		/// <summary>The player's own movement mode, on every captured player spawn.</summary>
		public const uint PlayerMovementMode = MovementModeRun;
	}
}
