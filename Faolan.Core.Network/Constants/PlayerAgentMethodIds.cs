using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     The PlayerAgent / PlayerInterface method - the <c>method_instance</c> (RpcHeader field 4) this one interface
	///     pair carries, and the catalog naming the opcodes retail is seen using.
	/// </summary>
	/// <remarks>
	///     The id is derived, not tabulated. The whole header is protobuf and the method is one varint field, which
	///     <c>Rpc</c> reads and writes whole; there is no <c>[u16 opcode][tail]</c> split. The value is also computed
	///     rather than stored - none of these ids appears in <c>AgeOfConan.exe</c>, because the client registers each
	///     method with a 32-bit constant and that constant is what goes on the wire. The enums in
	///     Faolan.Core/Enums/Opcodes.cs are those registration constants, so adding a method means copying one
	///     constant, not observing it on the wire.
	///     <para>
	///         Re-derive both tables from the client:
	///         <code>
	///         c2s: analyzeHeadless &lt;data-repo&gt;/Ghidra aoc -process AgeOfConan.exe -noanalysis \
	///                -scriptPath tools/ghidra -postScript DumpPlayerAgentCtor.java
	///         s2c: STR=AuthenticateAck MAX=3 analyzeHeadless ... -postScript StrXrefs.java
	///         </code>
	///         Both print <c>&lt;constant&gt;; FUN_00487900(); FUN_0040ec10("&lt;Name&gt;", &lt;len&gt;)</c> triples,
	///         the registration idiom.
	///     </para>
	///     <para>
	///         The 16-bit opcode is not a method key on this interface. <c>WireOpcode.Of</c> is a lossy view of the
	///         method varint - <c>0x2080 | (constant &amp; 0x7F)</c> here, since every constant needs more than one
	///         group - so two methods whose constants share their low 7 bits share an opcode, and both such pairs are
	///         in the corpus: 0x20EC is <c>ReceivedCharacterSettings</c> (0x3C7C926C) and <c>AuthenticateLoggedIn</c>
	///         (0x9D991E6C); 0x20D6 is <c>VerifyLanguageSetting</c> (0x9847AED6) and <c>UpdateCharacterPreview</c>
	///         (0x4D661656). Inbound dispatch keys on <c>Rpc.Method</c>; the lists below record what retail is
	///         observed sending, for naming only.
	///     </para>
	/// </remarks>
	public static class PlayerAgentMethodIds
	{
		// The client's registration constants, keyed by name. Derived from the `PlayerAgentMethods` /
		// `PlayerInterfaceMethods` enums (Faolan.Core/Enums/Opcodes.cs) so the constants live in exactly one place:
		// they are the typed catalog the inbound dispatch switches on, not just a table this file reads.
		public static readonly IReadOnlyDictionary<string, uint> PlayerAgentRegistration = Registration<PlayerAgentMethods>();

		public static readonly IReadOnlyDictionary<string, uint> PlayerInterfaceRegistration = Registration<PlayerInterfaceMethods>();

		// The methods the corpus shows, in each direction. Named rather than keyed by opcode: an opcode here is only the
		// method varint's first group and collides (0x20EC names two of these), so it cannot be a key. This is a record
		// of what retail is seen sending, not a general opcode->method function.
		public static readonly IReadOnlyList<PlayerInterfaceMethods> ObservedInterfaceMethods =
		[
			// s2c — the ones the server sends.
			PlayerInterfaceMethods.PlayerSetupComplete, PlayerInterfaceMethods.AuthenticateAck, PlayerInterfaceMethods.CharacterUnlocked,
			PlayerInterfaceMethods.OnFacebookTokenResult, PlayerInterfaceMethods.SendCharacterSettingsCheckSumMap, PlayerInterfaceMethods.SendCharacterSettings,
			PlayerInterfaceMethods.SetDimensionList, PlayerInterfaceMethods.SendCharacterPreview, PlayerInterfaceMethods.UpdateClientPlayerData,

			// The four login handoffs. They carry a full_routing_context (RpcHeader field 3) as well, whose tag and
			// length byte precede the method varint - so a naive read of the first two bytes sees the same value for
			// all four.
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

		/// <summary>
		///     The two bytes a hex dump shows for this method: a rendering of the constant rather than a fact about the
		///     method. It collides, so it can name a frame in a capture but must never key anything.
		/// </summary>
		/// <remarks>Named here for the login tier's reading of it; <c>WireOpcode.Of</c> is the general form.</remarks>
		public static ushort ObservedOpcode(uint methodInstance)
		{
			return WireOpcode.Of(methodInstance);
		}

		private static IReadOnlyDictionary<string, uint> Registration<T>() where T : struct, Enum
		{
			return Enum.GetValues<T>().ToDictionary(m => m.ToString()!, m => Convert.ToUInt32(m));
		}
	}
}
