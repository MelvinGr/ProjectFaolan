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

		// The opcode a method was OBSERVED under, for the methods the corpus shows. Only these are listed: an opcode
		// here is only the method varint's first group (see the warning above) and so collides, which is why this maps
		// what retail was seen doing rather than claiming a general opcode->method function.
		private static readonly IReadOnlyDictionary<ushort, string> ObservedOpcodes = new Dictionary<ushort, string>
		{
			// s2c PlayerInterface — the ones the server sends.
			[(ushort)PlayerInterfaceOpcodes.PlayerSetupComplete] = "PlayerSetupComplete",
			[(ushort)PlayerInterfaceOpcodes.AuthenticateAck] = "AuthenticateAck",
			[(ushort)PlayerInterfaceOpcodes.CharacterUnlocked] = "CharacterUnlocked",
			[(ushort)PlayerInterfaceOpcodes.OnFacebookTokenResult] = "OnFacebookTokenResult",
			[(ushort)PlayerInterfaceOpcodes.SendCharacterSettingsCheckSumMap] = "SendCharacterSettingsCheckSumMap",
			[(ushort)PlayerInterfaceOpcodes.SendCharacterSettings] = "SendCharacterSettings",
			[(ushort)PlayerInterfaceOpcodes.SetDimensionList] = "SetDimensionList",
			[(ushort)PlayerInterfaceOpcodes.SendCharacterPreview] = "SendCharacterPreview",
			[(ushort)PlayerInterfaceOpcodes.UpdateClientPlayerData] = "UpdateClientPlayerData",

			// The four login handoffs. They carry a full_routing_context (RpcHeader field 3) as well, which is why the
			// codebase used to read all four as one opcode `0x1A07` - that was field 3's tag plus its length byte, and
			// the method varint sat behind it (#610). Their opcodes are the ordinary view of their method constants.
			[(ushort)PlayerInterfaceOpcodes.ChatServerConnectReady] = "ChatServerConnectReady",
			[(ushort)PlayerInterfaceOpcodes.CSServerConnectReady] = "CSServerConnectReady",
			[(ushort)PlayerInterfaceOpcodes.LoginConnectReady] = "LoginConnectReady",
			[(ushort)PlayerInterfaceOpcodes.OverrideGameTweaks] = "OverrideGameTweaks",

			// c2s PlayerAgent — received, not sent; here for the dissector and the generated docs.
			[(ushort)PlayerAgentOpcodes.Authenticate] = "Authenticate",
			[(ushort)PlayerAgentOpcodes.FinishGetStartupData] = "FinishGetStartupData",
			[(ushort)PlayerAgentOpcodes.RequestDeleteCharacter] = "RequestDeleteCharacter",
			[(ushort)PlayerAgentOpcodes.UnlockCharacter] = "UnlockCharacter",
			[(ushort)PlayerAgentOpcodes.LoadFacebookToken] = "LoadFacebookToken",
			[(ushort)PlayerAgentOpcodes.LoginCharacter] = "LoginCharacter",
			[(ushort)PlayerAgentOpcodes.GetStartupData] = "GetStartupData",
			[(ushort)PlayerAgentOpcodes.VerifyLanguageSetting] = "VerifyLanguageSetting",
			[(ushort)PlayerAgentOpcodes.CreateCharacterWithData] = "CreateCharacterWithData",
			[(ushort)PlayerAgentOpcodes.ReceivedCharacterSettings] = "ReceivedCharacterSettings",
			[(ushort)PlayerAgentOpcodes.UpdateSubscriptionSettings] = "UpdateSubscriptionSettings"
		};

		// opcode -> the method's registration constant (`method_instance`), for the opcodes retail was observed using.
		// Read by `gen/opcodes.md`, which annotates those two enums with the id their opcode is only the first group of.
		public static IReadOnlyDictionary<ushort, uint> MethodInstanceByOpcode
		{
			get
			{
				var map = new Dictionary<ushort, uint>();
				foreach (var (opcode, method) in ObservedOpcodes)
					map[opcode] = Registration(method);
				return map;
			}
		}

		private static uint Registration(string method)
		{
			return PlayerAgentRegistration.TryGetValue(method, out var c) ? c : PlayerInterfaceRegistration[method];
		}

		private static IReadOnlyDictionary<string, uint> Registration<T>() where T : struct, Enum
		{
			return Enum.GetValues<T>().ToDictionary(m => m.ToString()!, m => Convert.ToUInt32(m));
		}
	}
}
