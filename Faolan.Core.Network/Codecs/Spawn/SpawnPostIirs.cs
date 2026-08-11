using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The from-scratch post-IIR streams. VALUE FACTORIES, not codec surface (#530 §2 rule 1 / the wave-3 precedent):
	// they name the semantic forms a spawn is built in, and SpawnPostIirCodec stays the one encoder.
	//
	// A FRESHLY-SPAWNED NO-MORPH NPC has the MINIMAL form: a single 41-byte stream (Npc(movementMode)).
	// APPEARANCE-bearing NPCs instead carry a morph table - the "*key=val" texscript string (e.g. "*ii=-1;*bl=-1")
	// followed by [u32 morphId][float32] pairs, decoded on Casilda (#243) and stored as morph rows. Emitting the
	// minimal form for such an NPC DROPS that table and the client renders it BALD
	// ([[npc_spawn_minimal_postiir_breaks_humanoids]]).
	public static class SpawnPostIirs
	{
		// The minimal post-IIR length, in bytes, for a freshly-spawned WORLD npc. Not the shortest form on the wire:
		// the 21 create-editor preview spawns carry a 40-byte one, identical except that its first record has no
		// move-task slot (firstRecordLen 4 rather than 5). Censused over all 11,632 captured streams (#475).
		public const int MinimalNpcLength = 41;

		// The three movement modes retail sends, censused over the 3,272 captured 41-byte forms: 1 x757, 2 x2492 and
		// 3 x23 (#475). They are the SAME domain as the character message's own `movement_mode` field - the two are
		// one value sent twice, see SpawnPostIir.MovementMode - so a spawn must carry the same number in both places
		// and NpcSpawnBuilder derives this one from the message rather than storing it a second time (#605).
		//
		// ⭐ WALK / RUN / SPRINT, named from the CLIENT rather than from the capture (#606). The mode picks a speed
		// out of the entity's movement-set block, and the block is the `<movementset>` XML (RDB 1010205) laid out in
		// its parser's own field order, so each mode's field triple names it outright:
		//
		//   mode | forward             | backward              | strafe
		//   1|4  | basewalkspeed       | basereversewalkspeed  | strafewalkspeed
		//   2|5  | baserunspeed        | basereverserunspeed   | straferunspeed
		//   3    | basesprintspeed     | basereverserunspeed   | straferunspeed
		//   6    | chargespeed x a factor
		//
		// Mode 3 reusing RUN's reverse and strafe is not a coincidence and is what pins it: the movementset has no
		// sprint reverse or sprint strafe attribute to read. 4 and 5 are an unnamed parallel pair that shares the
		// walk/run speeds exactly, and 6 is the vehicle/charge mode - none of the three has ever been seen on the
		// wire. The empirical anchor is `WorldState.RouteWalkSpeed` = 2.2 u/s, measured off retail's own patrollers
		// and exactly the Default Movementset's `basewalkspeed`, on NPCs we send as mode 1.
		//
		// Still `uint` rather than an enum on purpose: the client's switch has SIX arms and a `switch` over a
		// three-member enum would silently discard the rest - the `CharDCMove.MovingType` mistake.
		public const uint MovementModeWalk = 1;
		public const uint MovementModeRun = 2;
		public const uint MovementModeSprint = 3;

		// The value we send when a type carries no movement mode of its own. 2 is retail's modal value on 71% of
		// captured NPC spawns and on every captured player spawn, so it is inside retail's own distribution rather
		// than an invented default - which is the whole test #602 set for a per-spawn-event field.
		public const uint DefaultMovementMode = MovementModeRun;

		// Whether a value is one retail has ever sent in this slot. Used by the appearance classifier to tell a
		// minimal stream from a runtime-bearing one.
		public static bool IsMovementMode(uint value)
		{
			return value is MovementModeWalk or MovementModeRun or MovementModeSprint;
		}

		// A fresh NPC's stream: the move-task SLOT with no task in it (the one 0x00 byte that makes its first record
		// 5 bytes where a player's is 4), no buffs, `morphs` in the entry slot, and three empty closing pair lists.
		// The 29 bytes after the header used to be a baked `MinimalBody` array described as "the constant
		// terminators". They are not constants: they are an EMPTY buff list, an EMPTY morph table and three EMPTY
		// closing pair lists, four of whose framing words are ForCount(0) - which is 0x3F1, the exact face the 0x3F1
		// trap wears (see ListFrame).
		public static SpawnPostIir Npc(uint movementMode = DefaultMovementMode, IReadOnlyList<NpcAppearanceMorph>? morphs = null)
		{
			var slot = new NpcEntrySlot();
			if (morphs != null)
				slot.Morphs.AddRange(morphs);

			return new SpawnPostIir
			{
				MovementMode = movementMode,
				HasMoveTaskSlot = true,
				Slot = slot
			};
		}

		// A player's stream: no move-task slot at all, and the entry table / skill id list in the entry slot. The
		// movement mode is 2 on every captured player spawn - and so is the player's own `movement_mode` field, on all
		// 209 of them, which is the cross-family confirmation that the two are one value (#605).
		public static SpawnPostIir Player()
		{
			return new SpawnPostIir
			{
				MovementMode = PlayerMovementMode,
				HasMoveTaskSlot = false,
				Slot = new PlayerEntrySlot()
			};
		}

		// The player's own movement mode, on every captured player spawn.
		public const uint PlayerMovementMode = MovementModeRun;
	}
}
