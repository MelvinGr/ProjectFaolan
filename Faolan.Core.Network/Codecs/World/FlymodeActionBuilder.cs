namespace Faolan.Core.Network.Codecs.World
{
	// The GM FLY toggle, as a GameActionData element inside a GameActionListIIR (0xF508F4C1).
	//
	// Flying is CLIENT-SIDE state, not a wire field: the client models it as "alignment mode" 5 in its movement state
	// machine (`vehiclealignment.cpp`; the modes are 0=Normal 1=Climbing 2=Dead 3=Diving 4=Falling 5=Flying
	// 6=Knockback 7=Swimming, from the mode factory FUN_00a3cbc0). The setter FUN_00a29f20 writes the flag at
	// object+0x1BC and switches the mode; nothing about it rides CharDCMove, whose "MovingType" is a layout bitfield
	// with no movement-mode enum at all (docs/re/chardcmove-grammar.md).
	//
	// So the only server-side lever is the GameAction the client ships for exactly this:
	// `e_Type_GC_GameAction_StartFlymode`. Its wire type id is recovered from the descriptor table that
	// FUN_008bdf40 initialises (base 0x025077C8, stride 0x41): a registration writes the name pointer at
	// descriptor+0 and the enum id at descriptor+8, so the descriptor INDEX gives the type id as 0xCF09 + index.
	// StartFlymode registers at 0x025102EC => index 548 => type id 0xD12D (enum id 0xCF92). The derivation was
	// cross-checked against every entry of docs/re/gameaction_types.json that it overlaps: 49 names, 0 mismatches.
	// It is absent from that file only because the dump behind it scanned 0xCF09..0xD026 and this sits past the end -
	// the same reason ProxyTeleport (0xD04D) is missing.
	//
	// ⚠️ NOTE: the client can already do this WITHOUT us. `SkywalkingCommand` is a plain client-local chat command -
	// verb `/luke` (Luke Skywalker; the verb is an LDB string, which is why nothing in the exe greps as "fly") - and
	// its execute FUN_013f6130 sends no packet: it toggles the same object+0x1BC flag and alignment mode. So `/luke`
	// is the decoded, working route, and this GameAction is the SERVER-driven alternative, kept because a server that
	// wants to grant/revoke flight (rather than the player toggling it) needs a lever of its own.
	//
	// ⚠️ The PARAMETER list is unverified. Key lists live in the runtime-filled descriptor table (BSS), so they come
	// from a live Frida dump, and StartFlymode is not in ours. An empty key list - nothing after the four fixed ints -
	// is the shape of a parameterless toggle and is what this sends; it is an assumption awaiting an in-client check,
	// not a decoded fact. If the client rejects or ignores it, dump the descriptor's key list from a live client
	// before guessing further.
	public static class FlymodeActionBuilder
	{
		// e_Type_GC_GameAction_StartFlymode. See the class comment for the derivation.
		public const uint StartFlymodeType = 0xD12D;

		// Builds the StartFlymode body delivered to `frameHandle` (the receiving player's instance). Unlike the
		// construct-only recast builder this is actually SENT, so it mirrors what real captured elements carry rather
		// than the ctor defaults: 0x2f5 = 9 (all four captured bodies) and the ReadSubClass [u8][u32] trailer (every
		// captured body ends in those five zero bytes).
		public static byte[] BuildStartFlymodeBody(uint frameHandle)
		{
			return GameActionElement.BuildBody(frameHandle, StartFlymodeType, 0, 0, [],
				GameActionElement.CapturedSeed0x2f5, trailer: true);
		}
	}
}
