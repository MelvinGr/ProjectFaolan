using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// The 32-bit method id that PlayerAgent (c2s) / PlayerInterface (s2c) — uniquely among the interfaces — carries
	// in its header right after the 16-bit opcode (`ConanPacket.HeaderData`, empty for every other interface). The
	// client dispatches on this id; the opcode is a normal 0x2000+index value alongside it. This is the SINGLE source
	// of the ids: the enums keep only the opcode, the PlayerAgent PlayerRpc helper sets it as the Rpc HeaderData, and
	// `gen/opcodes.md` reads it for its id column. Keyed by the wire opcode (values don't collide across the two
	// directions). Every id is capture-verified — the opcode->id pair was observed on the wire across the captures
	// (docs/re/aoc-rpc-method-opcodes.md, #320).
	//
	// TWO methods are deliberately absent because retail does NOT send the standard 4-byte id for them (confirmed in
	// the dumps):
	//   - PlayerInterfaceOpcodes.SmallCharList (0x20A5): a 3-byte header-extra `94 A7 60` (88/88 captures), not a
	//     4-byte id — sent verbatim at its call site.
	//   - PlayerInterfaceOpcodes.OverrideGameTweaks: not on the PlayerInterface endpoint at all — it rides
	//     the ServiceConnection with a 13-byte session-specific header-extra — sent verbatim at its call site.
	public static class PlayerAgentMethodIds
	{
		public static readonly IReadOnlyDictionary<ushort, uint> ById = new Dictionary<ushort, uint>
		{
			// c2s PlayerAgent (received by the server; here for the dissector + gen)
			[(ushort)PlayerAgentOpcodes.Authenticate] = 0x96CBE509,
			[(ushort)PlayerAgentOpcodes.FinishGetStartupData] = 0xA3E1FE0D,
			[(ushort)PlayerAgentOpcodes.DeleteCharacter] = 0x86979E0C,
			[(ushort)PlayerAgentOpcodes.LoadFacebookToken] = 0xE0C19C0D,
			[(ushort)PlayerAgentOpcodes.LoginCharacter] = 0xDD85FB0E,
			[(ushort)PlayerAgentOpcodes.GetStartupData] = 0xDAD1D206,
			[(ushort)PlayerAgentOpcodes.VerifyLanguageSetting] = 0xDD9EC209,
			[(ushort)PlayerAgentOpcodes.CreateCharacter] = 0x9CB1D10C,
			[(ushort)PlayerAgentOpcodes.ReceivedCharacterSettings] = 0xA4F2E303,

			// s2c PlayerInterface (sent by the server)
			[(ushort)PlayerInterfaceOpcodes.SendCharacterRealmData] = 0xCBC6FC04,
			[(ushort)PlayerInterfaceOpcodes.InitAuth] = 0x9386EE05,
			[(ushort)PlayerInterfaceOpcodes.Ox2093] = 0xF2A5E705,
			[(ushort)PlayerInterfaceOpcodes.Ox209C] = 0x99959205,
			[(ushort)PlayerInterfaceOpcodes.Ox20B9] = 0x8BD89902,
			[(ushort)PlayerInterfaceOpcodes.SendRealmList] = 0xE2E6C40F,
			[(ushort)PlayerInterfaceOpcodes.Ox20E5] = 0xDDAD8302,
			[(ushort)PlayerInterfaceOpcodes.SendCharacterList] = 0x8BD3A00C
		};

		// The 4-byte big-endian header id for `opcode`, or null if it carries none (the two special-case methods
		// above, and any opcode not in this interface).
		public static byte[]? HeaderId(ushort opcode)
		{
			return ById.TryGetValue(opcode, out var id)
				? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
				: null;
		}
	}
}
