using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

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
	//    (`>> 7` because the varint's first group rides in the opcode's low byte, not because the client shifts
	//    anything: reassembled, the field is the constant whole. See RpcHeaderData and Gitea #610.)
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
	// ⚠️ **The 16-bit opcode is NOT a method key on this interface** — and the reason is not that it is per-connection,
	// which is what it looks like until the varint is reassembled (see RpcHeaderData). It is `0x2080 | (constant &
	// 0x7F)`, so two methods whose constants share their low 7 bits share an opcode, and both such pairs are in the
	// corpus: `0x20EC` is `ReceivedCharacterSettings` (0x3C7C926C) AND `AuthenticateLoggedIn` (0x9D991E6C); `0x20D6`
	// is `VerifyLanguageSetting` (0x9847AED6) AND `UpdateCharacterPreview` (0x4D661656). Our inbound switch keys on
	// the opcode alone, so it cannot tell either pair apart — tracked as Gitea #610.
	public static class PlayerAgentMethodIds
	{
		// Field 4 of the endpoint sub-message, varint - the method constant. ProtoEndpoint itself stops at field 3
		// (token), which is why this one is named here rather than there.
		private const byte MethodReferenceTag = 0x20;

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
		// here is only the varint's first group (see the warning above) and so collides, which is why this maps what
		// retail was seen doing rather than claiming a general opcode->method function.
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

		// The header bytes for `opcode`, or null when this interface does not carry one for it. Opcode 0x1A07 is the one
		// exception in the whole scheme and is served by MethodReference below, not from here.
		public static byte[]? HeaderId(ushort opcode)
		{
			return ObservedOpcodes.TryGetValue(opcode, out var method)
				? Varint(WireId(Registration(method)))
				: null;
		}

		// The METHOD REFERENCE that PlayerInterface opcode `0x1A07` carries where every other method carries its id.
		// This is what the four 13-byte "opaque session blobs" in the login handoff actually are (Gitea #609): the
		// receiver endpoint restated, plus a FOURTH field holding the target method's registration constant WHOLE -
		//
		//     [0x0D <PlayerInterface type, fixed32 LE>][0x10 0x01 (instance)][0x20 varint(registrationConstant)]
		//
		// Note `varint(constant)`, not `varint(constant >> 7)`: the >> 7 form is the SHORT id an opcode already
		// half-names, and there is no opcode here to name it with. So `0x1A07` is not "OverrideGameTweaks" - it is a
		// generic carrier whose header tail selects the PlayerInterface method that fires, and the four tails in the
		// corpus (118 sightings each) decode to ChatServerConnectReady, CSServerConnectReady, LoginConnectReady and
		// OverrideGameTweaks - exactly the four the login handoff sends, in the order it sends them.
		public static byte[] MethodReference(string method)
		{
			var constant = Varint(Registration(method));
			var reference = new byte[8 + constant.Length];
			reference[0] = (byte)ProtoEndpoint.FieldTags.Type;
			BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(1), Endpoints.PlayerInterfaceType);
			reference[5] = (byte)ProtoEndpoint.FieldTags.Instance;
			reference[6] = 0x01;
			reference[7] = MethodReferenceTag;
			constant.CopyTo(reference, 8);
			return reference;
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
