using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Protocol
{
	// The single source of truth for turning a wire opcode into an interface-scoped enum/name. A 16-bit opcode is a
	// method INDEX, so the same number names a different method on each interface/agent (0x2000 = InitiateAuthentication
	// on UniverseAgent, Authenticate on CsPlayerAgent; 0x2009 = DispatchGameServerStats on GameInterface,
	// DispatchChangeItemStackSize on GameCharInterface). The endpoint's Type (the interface/agent-type hash)
	// disambiguates, so the (Receiver.Type, opcode) pair uniquely identifies the method. Used by both packet loggers:
	// inbound (UL) receivers are the *Agent endpoints (c2s), outbound (DL) receivers are the *Interface endpoints (s2c).
	public static class ProtocolCatalog
	{
		// (interface/agent-type hash) -> the *Opcodes enum whose members are that endpoint's methods. Covers the c2s
		// *Agent classes (inbound naming) and the s2c *Interface classes (outbound naming). A 16-bit opcode never
		// collides ACROSS this map because the receiver Type keys it.
		private static readonly IReadOnlyDictionary<uint, Type> OpcodeEnumsByReceiverType = new Dictionary<uint, Type>
		{
			// c2s: the client addresses the server's *Agent endpoint (the inbound UL trace).
			[Endpoints.UniverseAgentType] = typeof(UniverseAgentOpcodes),
			[Endpoints.PlayerAgentType] = typeof(PlayerAgentOpcodes),
			[Endpoints.CSPlayerAgentType] = typeof(CSPlayerAgentOpcodes),
			[Endpoints.GameAgentType] = typeof(GameAgentOpcodes),
			[Endpoints.GameCharAgentType] = typeof(GameCharAgentOpcodes),
			[Endpoints.GameCharAgentProjectType] = typeof(GameCharAgentProjectOpcodes),
			[Endpoints.TradepostAgentType] = typeof(TradepostAgentOpcodes),

			// s2c: the server addresses the client's *Interface endpoint (the outbound DL trace).
			[Endpoints.GameInterfaceType] = typeof(GameInterfaceOpcodes),
			[Endpoints.GameCharInterfaceType] = typeof(GameCharInterfaceOpcodes),
			[Endpoints.GameCharInterfaceProjectType] = typeof(GameCharInterfaceProjectOpcodes),
			[Endpoints.TradepostInterfaceType] = typeof(TradepostInterfaceOpcodes),
			[Endpoints.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceOpcodes)
		};

		// The opcode enum for a given receiver-type hash, but only when that enum actually DEFINES the opcode; else null
		// so the caller falls back to its own default/expected enum. Both conditions matter: a receiver type we don't
		// map, and an opcode the mapped enum doesn't carry, are equally "not resolvable here". Used by the DL logger's
		// fall-back-to-listener-default naming.
		public static Type? OpcodeEnumFor(uint receiverType, ushort opcode) =>
			OpcodeEnumsByReceiverType.TryGetValue(receiverType, out var enumType) && Enum.IsDefined(enumType, opcode)
				? enumType
				: null;

		// The (Receiver.Type, opcode) pair -> "MethodName (0xHEX)". THE naming entry point, used by both directions of the
		// packet trace, so an RX line and a TX line name the same method the same way. The receiver Type already selects
		// the right class catalog, so nothing has to thread a typed opcode enum in from the listener.
		//
		// `fallbackEnum` covers the receiver classes the catalog doesn't map - the login tier's client-side *Interface
		// classes, which the server addresses on the way OUT. Pass it only for a direction whose class family the enum
		// actually belongs to: an opcode is a method INDEX, so naming an inbound (c2s) method out of an s2c interface
		// enum would confidently print the WRONG method. That is why the RX side passes none and renders "Unknown".
		public static string ResolveName(uint receiverType, ushort opcode, Type? fallbackEnum = null)
		{
			var enumType = OpcodeEnumFor(receiverType, opcode) ?? (fallbackEnum is not null && Enum.IsDefined(fallbackEnum, opcode)
				? fallbackEnum
				: null);

			return enumType is not null
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: $"Unknown ({opcode.ToHex()})";
		}
	}
}
