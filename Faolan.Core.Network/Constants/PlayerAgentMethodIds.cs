using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// The PlayerAgent / PlayerInterface METHOD — the `method_instance` (RpcHeader field 4) this one interface pair
	// carries, and the catalog that names the opcodes retail was seen using. Gitea #320/#610.
	//
	// ## The id is derived, not tabulated
	//
	// Two things were written down about this field, and both were wrong in a way that made the interface look
	// un-enumerable:
	//
	// 1. **It is not a fixed 4-byte id after a 16-bit opcode.** The whole header is protobuf and the method is one
	//    varint field; `Rpc` reads and writes it whole (see Rpc.Parse). What used to be read as a `[u16 opcode][tail]`
	//    split was that varint cut after its first group, which is why the last byte of a "4-byte tail" is always small
	//    (0x02..0x0F) — it is the top of a 32-bit value, not the low byte of a hash.
	// 2. **The value is computed, not stored.** None of these ids appears anywhere in `AgeOfConan.exe`, which is what
	//    made "just dump the table" a dead end. The client registers each method with a 32-bit constant, and that
	//    constant IS what goes on the wire.
	//
	// So the enums below (Faolan.Core/Enums/Opcodes.cs) are the client's own registration constants, and there is no
	// encoding step left. Adding a method means copying one constant, not observing it on the wire.
	//
	// Re-derive both tables from the client:
	//   c2s: analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
	//          -scriptPath tools/ghidra -postScript DumpPlayerAgentCtor.java
	//   s2c: STR=AuthenticateAck MAX=3 analyzeHeadless ... -postScript StrXrefs.java
	// Both print `<constant>; FUN_00487900(); FUN_0040ec10("<Name>", <len>)` triples — the registration idiom.
	//
	// ⚠️ **The 16-bit opcode is NOT a method key on this interface.** `Rpc.Opcode` is a lossy view of the method varint
	// — `0x2080 | (constant & 0x7F)` here, since every constant needs more than one group — so two methods whose
	// constants share their low 7 bits share an opcode, and both such pairs are in the corpus: `0x20EC` is
	// `ReceivedCharacterSettings` (0x3C7C926C) AND `AuthenticateLoggedIn` (0x9D991E6C); `0x20D6` is
	// `VerifyLanguageSetting` (0x9847AED6) AND `UpdateCharacterPreview` (0x4D661656). Inbound dispatch keys on
	// `Rpc.Method` (#608); the map below is what retail was OBSERVED sending, for naming only.
	public static class PlayerAgentMethodIds
	{
		// The client's registration constants, keyed by name. Derived from the `PlayerAgentMethods` /
		// `PlayerInterfaceMethods` enums (Faolan.Core/Enums/Opcodes.cs) so the constants live in exactly one place -
		// they are a typed catalog the inbound dispatch switches on (#608), not just a table this file reads.
		public static readonly IReadOnlyDictionary<string, uint> PlayerAgentRegistration = Registration<PlayerAgentMethods>();

		public static readonly IReadOnlyDictionary<string, uint> PlayerInterfaceRegistration = Registration<PlayerInterfaceMethods>();

		// The methods the corpus actually shows, in each direction. Named rather than keyed by opcode: an opcode here is
		// only the method varint's first group and COLLIDES (`0x20EC` names two of these), so it cannot be a key - which
		// is why the `PlayerAgentOpcodes`/`PlayerInterfaceOpcodes` enums were deleted in #613. This is a record of what
		// retail was seen sending, not a general opcode->method function.
		public static readonly IReadOnlyList<PlayerInterfaceMethods> ObservedInterfaceMethods =
		[
			// s2c — the ones the server sends.
			PlayerInterfaceMethods.PlayerSetupComplete, PlayerInterfaceMethods.AuthenticateAck, PlayerInterfaceMethods.CharacterUnlocked,
			PlayerInterfaceMethods.OnFacebookTokenResult, PlayerInterfaceMethods.SendCharacterSettingsCheckSumMap, PlayerInterfaceMethods.SendCharacterSettings,
			PlayerInterfaceMethods.SetDimensionList, PlayerInterfaceMethods.SendCharacterPreview, PlayerInterfaceMethods.UpdateClientPlayerData,

			// The four login handoffs. They carry a full_routing_context (RpcHeader field 3) as well, which is why the
			// codebase used to read all four as one opcode `0x1A07` - that was field 3's tag plus its length byte, and
			// the method varint sat behind it (#610).
			PlayerInterfaceMethods.ChatServerConnectReady, PlayerInterfaceMethods.CSServerConnectReady,
			PlayerInterfaceMethods.LoginConnectReady, PlayerInterfaceMethods.OverrideGameTweaks
		];

		// c2s — received, not sent; here for the dissector and the generated docs.
		public static readonly IReadOnlyList<PlayerAgentMethods> ObservedAgentMethods =
		[
			PlayerAgentMethods.Authenticate, PlayerAgentMethods.FinishGetStartupData, PlayerAgentMethods.RequestDeleteCharacter,
			PlayerAgentMethods.UnlockCharacter, PlayerAgentMethods.LoadFacebookToken, PlayerAgentMethods.LoginCharacter,
			PlayerAgentMethods.GetStartupData, PlayerAgentMethods.VerifyLanguageSetting, PlayerAgentMethods.CreateCharacterWithData,
			PlayerAgentMethods.ReceivedCharacterSettings, PlayerAgentMethods.UpdateSubscriptionSettings
		];

		// The two bytes you would read off a hex dump for this method: the method field's tag, then its varint's first
		// group. COMPUTED, because it is a rendering of the constant rather than a fact about the method - it collides,
		// so it can name a frame in a capture but must never key anything. `Rpc.Opcode` refuses to return it for these
		// methods at all (#613); this is the display-only escape hatch, used by `gen/opcodes.md`.
		public static ushort ObservedOpcode(uint methodInstance)
		{
			return (ushort)(0x2000 | (methodInstance & 0x7F) | (methodInstance > 0x7F ? 0x80u : 0u));
		}

		private static IReadOnlyDictionary<string, uint> Registration<T>() where T : struct, Enum
		{
			return Enum.GetValues<T>().ToDictionary(m => m.ToString()!, m => Convert.ToUInt32(m));
		}
	}
}
