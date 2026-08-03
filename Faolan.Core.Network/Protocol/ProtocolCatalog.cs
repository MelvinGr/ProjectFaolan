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

		// The two classes that register their methods by 32-bit HASH rather than by index, and so have no opcode at all:
		// `Rpc.Opcode` would keep 7 of those bits and throws instead (#613), since `0x20EC` names both
		// ReceivedCharacterSettings and AuthenticateLoggedIn (#608). A frame on one of these is named from Rpc.Method.
		// There is deliberately no `*Opcodes` enum for this pair to sit in the map above.
		private static readonly IReadOnlyDictionary<uint, Type> MethodEnumsByReceiverType = new Dictionary<uint, Type>
		{
			[Endpoints.PlayerAgentType] = typeof(PlayerAgentMethods),
			[Endpoints.PlayerInterfaceType] = typeof(PlayerInterfaceMethods)
		};

		// The opcode enum for a given receiver-type hash, but only when that enum actually DEFINES the opcode; else null
		// so the caller falls back to its own default/expected enum. Both conditions matter: a receiver type we don't
		// map, and an opcode the mapped enum doesn't carry, are equally "not resolvable here". Used by the DL logger's
		// fall-back-to-listener-default naming.
		public static Type? OpcodeEnumFor(uint receiverType, ushort opcode)
		{
			return OpcodeEnumsByReceiverType.TryGetValue(receiverType, out var enumType) && Enum.IsDefined(enumType, opcode)
				? enumType
				: null;
		}

		// The (Receiver.Type, opcode) pair -> "MethodName (0xHEX)". THE naming entry point, used by both directions of the
		// packet trace, so an RX line and a TX line name the same method the same way. The receiver Type already selects
		// the right class catalog, so nothing has to thread a typed opcode enum in from the listener.
		//
		// `fallbackEnum` covers the receiver classes the catalog doesn't map - the login tier's client-side *Interface
		// classes, which the server addresses on the way OUT. Pass it only for a direction whose class family the enum
		// actually belongs to: an opcode is a method INDEX, so naming an inbound (c2s) method out of an s2c interface
		// enum would confidently print the WRONG method. That is why the RX side passes none and renders "Unknown".
		// The name for a whole frame - what the packet trace calls it. Identical to the (receiver class, opcode) form
		// below except on a hash-registered method, which has no opcode to name it with.
		//
		// The branch is on the METHOD, not on the receiver class, and that matters: the four login handoffs address the
		// ServiceConnection rather than either class in the map above, so a receiver-keyed test would fall through to
		// `rpc.Opcode` - which now throws for exactly these (#613). Their `fallbackEnum` (PlayerInterfaceMethods) names
		// them instead.
		public static string ResolveName(in Rpc rpc, Type? fallbackEnum = null)
		{
			if (rpc.HasOpcode)
				return ResolveName(rpc.Receiver.Type, rpc.Opcode, fallbackEnum);

			var methodEnum = MethodEnumsByReceiverType.TryGetValue(rpc.Receiver.Type, out var byReceiver) && Enum.IsDefined(byReceiver, rpc.Method)
				? byReceiver
				: Defining(fallbackEnum, rpc.Method);

			return methodEnum is not null
				? $"{Enum.GetName(methodEnum, rpc.Method)} ({rpc.Method.ToHex()})"
				: $"Unknown ({rpc.Method.ToHex()})";
		}

		public static string ResolveName(uint receiverType, ushort opcode, Type? fallbackEnum = null)
		{
			var enumType = OpcodeEnumFor(receiverType, opcode) ?? Defining(fallbackEnum, opcode);

			return enumType is not null
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: $"Unknown ({opcode.ToHex()})";
		}

		// `fallbackEnum`, but only when it both DEFINES the value and is backed by the value's own width. The width
		// check is not defensive noise: the login tier's fallback is now a uint-backed method enum while the opcode
		// overload passes a ushort, and Enum.IsDefined THROWS on that mismatch rather than returning false.
		private static Type? Defining(Type? fallbackEnum, object value)
		{
			return fallbackEnum is not null && Enum.GetUnderlyingType(fallbackEnum) == value.GetType() && Enum.IsDefined(fallbackEnum, value)
				? fallbackEnum
				: null;
		}
	}
}
