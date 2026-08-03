using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// The PlayerAgent / PlayerInterface METHOD ID — the value this one interface pair carries in its RPC header after
	// the 16-bit opcode (`Rpc.HeaderData`). Gitea #320.
	//
	// ## It is a VARINT, and it is derived, not tabulated
	//
	// Two things were written down about this header field, and both were wrong in a way that made the interface look
	// un-enumerable:
	//
	// 1. **It is not a fixed 4-byte id.** It is a protobuf VARINT. All 22 distinct header tails in the capture corpus
	//    are valid varints — 4-byte ones for most methods, but also the 3-byte `94 A7 60` and the 1-byte `01` that the
	//    grammar used to list as two separate "constant" cases. That is why the last byte is always small (0x02..0x0F):
	//    it is the top bits of a 25-bit value, not the low byte of a hash.
	// 2. **The value is computed, not stored.** None of these ids appears anywhere in `AgeOfConan.exe`, which is what
	//    made "just dump the table" a dead end. The client registers each method with a 32-bit constant, and what goes
	//    on the wire is `staticId >> 7`. That rule reproduces **all 22** observed ids — 13 c2s and 9 s2c — exactly.
	//
	// So the table below is the client's own registration constants, and the wire encoding is a one-line function of
	// them. Adding a method means copying one constant, not observing it on the wire.
	//
	// Re-derive both tables from the client:
	//   c2s: analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
	//          -scriptPath tools/ghidra -postScript DumpPlayerAgentCtor.java
	//   s2c: STR=AuthenticateAck MAX=3 analyzeHeadless ... -postScript StrXrefs.java
	// Both print `<constant>; FUN_00487900(); FUN_0040ec10("<Name>", <len>)` triples — the registration idiom.
	//
	// ⚠️ **The 16-bit opcode is NOT a method key on this interface.** It is a per-connection registration index, and
	// the corpus proves it: on a second PlayerAgent connection, opcode `0x20EC` carries `AuthenticateLoggedIn` where
	// the login connection had `ReceivedCharacterSettings`, and `0x20D6` carries `UpdateCharacterPreview` where it had
	// `VerifyLanguageSetting`. The client dispatches on the method id, which is why this interface never fitted the
	// `0x2000 + index` model. Our inbound switch still keys on the opcode — tracked separately.
	public static class PlayerAgentMethodIds
	{
		// c2s: `PlayerAgentProxy_t`'s registration constants, in registration order. All 25 the client has.
		public static readonly IReadOnlyDictionary<string, uint> PlayerAgentRegistration = new Dictionary<string, uint>
		{
			["Authenticate"] = 0x9CB2CB03,
			["AuthenticateLoggedIn"] = 0x9D991E6C,
			["LoginFromCrash"] = 0xCBDE126E,
			["CreateCharacter"] = 0xBE735486,
			["CreateCharacterWithData"] = 0xCA2C4E5E,
			["LoginCharacter"] = 0xEF616EB6,
			["RenameCharacter"] = 0x1B2CF268,
			["RequestDeleteCharacter"] = 0xC3C5C31D,
			["VerifyLanguageSetting"] = 0x9847AED6,
			["AddOfflineLevel"] = 0x2976E1B7,
			["UnlockCharacter"] = 0x9C370D1E,
			["SetHardcoreLevel"] = 0x6A6B621C,
			["GetDimensionList"] = 0x43476328,
			["RequestSuggestNickname"] = 0xDBE172E5,
			["GetStartupData"] = 0x6A546D41,
			["LoadFacebookToken"] = 0xD3907026,
			["SaveFacebookToken"] = 0xD8AD362B,
			["UpdateFacebookFriends"] = 0x3A96B2B3,
			["RecruitFacebookFriends"] = 0x5B51044B,
			["UpdateSubscriptionSettings"] = 0x516C9EF5,
			["UpdateCharacterSettings"] = 0x01035784,
			["UpdateCharacterPreview"] = 0x4D661656,
			["FinishGetStartupData"] = 0xDFD8518E,
			["ReceivedCharacterSettings"] = 0x3C7C926C,
			["ClientReplyKeepCookie"] = 0x43D90C1B
		};

		// s2c: `PlayerInterface`'s registration constants. All 26.
		public static readonly IReadOnlyDictionary<string, uint> PlayerInterfaceRegistration = new Dictionary<string, uint>
		{
			["OnFacebookTokenResult"] = 0x52454C9C,
			["OnUpdateFacebookFriends"] = 0x88750390,
			["OnFacebookFriendSignedUp"] = 0x7955BD80,
			["AuthenticateAck"] = 0x5DC18991,
			["LoginConnectReady"] = 0xD8B66C79,
			["CSServerConnectReady"] = 0x5AED2A60,
			["ChatServerConnectReady"] = 0x23A632FA,
			["UpdateClientPlayerData"] = 0xC414C5EF,
			["SetDimensionList"] = 0xF899B14C,
			["SetStats"] = 0xA7C86A29,
			["SuggestNicknameResult"] = 0xE8EAD4F8,
			["PlayerSetupComplete"] = 0x4F91A58C,
			["LoginQueuePosition"] = 0x0FE09F03,
			["OfflineLevelApplied"] = 0x3E8F0623,
			["CharacterUnlocked"] = 0x5CE97913,
			["HardcoreLevelSet"] = 0x1562CE84,
			["SendSubscriptionSettings"] = 0x15F7AF22,
			["SendCharacterSettings"] = 0x233605B9,
			["SendCharacterPreview"] = 0x206B6EE5,
			["SendCharacterSettingsCheckSumMap"] = 0x0C09CA25,
			["VerifyClientStillConnected"] = 0x00C46317,
			["OverrideGameTweaks"] = 0x7C47F763,
			["CharacterNeedsNewName"] = 0x9625EB4A,
			["LoginProblem"] = 0xD4063CA0,
			["TeleportProblem"] = 0xE33EBAE1,
			["ResetLanguage"] = 0xD4C59816
		};

		// The opcode a method was OBSERVED under, for the methods the corpus shows. Only these are asserted: an opcode
		// is a per-connection index (see the warning above), so this maps what retail's login connection actually used
		// rather than claiming a general opcode->method function.
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

		// The wire method id: the client's registration constant with its low 7 bits dropped. Verified against all 22
		// distinct header tails in the corpus - there is no exception.
		public static uint WireId(uint registrationConstant)
		{
			return registrationConstant >> 7;
		}

		// opcode -> wire method id, for the opcodes retail was observed using. Read by `gen/opcodes.md` and the
		// dissector.
		public static IReadOnlyDictionary<ushort, uint> ById
		{
			get
			{
				var map = new Dictionary<ushort, uint>();
				foreach (var (opcode, method) in ObservedOpcodes)
					map[opcode] = WireId(Registration(method));
				return map;
			}
		}

		// The header bytes for `opcode`, or null when this interface does not carry one for it. `OverrideGameTweaks` is
		// the one genuine exception in the whole scheme: it rides the ServiceConnection endpoint with a 13-byte
		// protobuf endpoint reference instead of its method id, so its call site passes that verbatim.
		public static byte[]? HeaderId(ushort opcode)
		{
			return ObservedOpcodes.TryGetValue(opcode, out var method)
				? Varint(WireId(Registration(method)))
				: null;
		}

		// Base-128 varint, low group first with the continuation bit on every group but the last - the protobuf
		// encoding the rest of the header already uses for its endpoint fields.
		public static byte[] Varint(uint value)
		{
			var bytes = new List<byte>(5);
			while (true)
			{
				var group = (byte)(value & 0x7F);
				value >>= 7;
				if (value == 0)
				{
					bytes.Add(group);
					return bytes.ToArray();
				}

				bytes.Add((byte)(group | 0x80));
			}
		}

		private static uint Registration(string method)
		{
			return PlayerAgentRegistration.TryGetValue(method, out var c) ? c : PlayerInterfaceRegistration[method];
		}
	}
}
