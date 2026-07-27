namespace Faolan.Core.Network.Constants
{
	// The 8-byte NULL IDENTITY sentinel `B2 90 80 5C 29 E6 27 CA` - the value the client's identity/ownership slots
	// carry when there is nothing to point at. It is the wire's "unset", not a live id: it is byte-identical in every
	// capture, across unrelated subsystems, and never varies with the character, item, ability or session.
	//
	// It had FOUR names in four codecs before this existed, each guessing a different meaning from its own capture:
	//   AbilityGrantBody      AddSpecialAbilityConstantA/B  "a fixed category/owner word"
	//   BehaviorAckBody       NullIdentityFiller            "the equip-ack's null identity filler"
	//   CreateEditorRecordBuilder  BehaviorOwnerStamp       "the fixed creator/timestamp pair (semantics unpinned)"
	//   TradeskillRecipeCodec ElementConst                  (unnamed - "element [13..16]")
	//
	// The shape is what settles it. Every one of those sites writes the sentinel TWICE in a row, and the two that
	// carry surrounding context write it immediately after a block that is ALSO doubled - the ability grant repeats
	// `Key1..Key4` then writes the pair twice; the create-editor record repeats the 16-byte transform then writes the
	// pair twice. So these are slots for TWO identities, both unset, in records whose fields come in pairs - not a
	// 16-byte "constant", a category word, or a timestamp. `Pair` is that doubled form, so a caller writes what the
	// record actually holds instead of spelling the value twice.
	//
	// ⚠️ Do NOT read it as an owner id and try to fill it in with a real one: no capture has ever shown a non-sentinel
	// value in any of these slots, so what a populated identity looks like here is unknown. [V0]
	public static class NullIdentity
	{
		public const uint High = 0xB290805C;
		public const uint Low = 0x29E627CA;

		// The sentinel as its 8 wire bytes.
		public static readonly byte[] Bytes = [0xB2, 0x90, 0x80, 0x5C, 0x29, 0xE6, 0x27, 0xCA];

		// Two consecutive unset identities - the form every known site writes.
		public static readonly uint[] Pair = [High, Low, High, Low];
	}
}
