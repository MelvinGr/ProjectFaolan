using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Protocol
{
	// The single source of truth for turning a wire method into an interface-scoped enum/name. On the index-registered
	// classes a method is a registration INDEX, so the same number names a different method on each interface/agent
	// (0x00 = InitiateAuthentication on UniverseAgent, Authenticate on CsPlayerAgent; 0x09 = DispatchGameServerStats on
	// GameInterface, DispatchChangeItemStackSize on GameCharInterface). The endpoint's Type (the interface/agent-type
	// hash) disambiguates, so the (Receiver.Type, method) pair uniquely identifies the method. Used by both packet
	// loggers: inbound (UL) receivers are the *Agent endpoints (c2s), outbound (DL) receivers are the *Interface
	// endpoints (s2c).
	public static class ProtocolCatalog
	{
		// (interface/agent-type hash) -> the *Methods enum whose members are that endpoint's methods. Covers the c2s
		// *Agent classes (inbound naming) and the s2c *Interface classes (outbound naming). A method index never
		// collides ACROSS this map because the receiver Type keys it.
		//
		// The hash-registered PlayerAgent/PlayerInterface pair sits in the SAME map, which it could not before #615:
		// its members are 32-bit registration hashes rather than indices, and while the other enums were written as
		// `0x2000 + index` the two spaces overlapped and had to be kept apart. Now every member IS its own
		// `method_instance` constant, indices are <= 0xD2 and hashes are 32-bit, so one map and one lookup serve both.
		private static readonly IReadOnlyDictionary<uint, Type> MethodEnumsByReceiverType = new Dictionary<uint, Type>
		{
			// c2s: the client addresses the server's *Agent endpoint (the inbound UL trace).
			[Endpoints.UniverseAgentType] = typeof(UniverseAgentMethods),
			[Endpoints.CSPlayerAgentType] = typeof(CSPlayerAgentMethods),
			[Endpoints.GameAgentType] = typeof(GameAgentMethods),
			[Endpoints.GameCharAgentType] = typeof(GameCharAgentMethods),
			[Endpoints.GameCharAgentProjectType] = typeof(GameCharAgentProjectMethods),
			[Endpoints.TradepostAgentType] = typeof(TradepostAgentMethods),
			[Endpoints.PlayerAgentType] = typeof(PlayerAgentMethods),

			// s2c: the server addresses the client's *Interface endpoint (the outbound DL trace).
			[Endpoints.GameInterfaceType] = typeof(GameInterfaceMethods),
			[Endpoints.GameCharInterfaceType] = typeof(GameCharInterfaceMethods),
			[Endpoints.GameCharInterfaceProjectType] = typeof(GameCharInterfaceProjectMethods),
			[Endpoints.TradepostInterfaceType] = typeof(TradepostInterfaceMethods),
			[Endpoints.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceMethods),
			[Endpoints.PlayerInterfaceType] = typeof(PlayerInterfaceMethods)
		};

		// The method enum for a given receiver-type hash, but only when that enum actually DEFINES the method; else null
		// so the caller falls back to its own default/expected enum. Both conditions matter: a receiver type we don't
		// map, and a method the mapped enum doesn't carry, are equally "not resolvable here". Used by the DL logger's
		// fall-back-to-listener-default naming.
		public static Type? MethodEnumFor(uint receiverType, uint method)
		{
			return MethodEnumsByReceiverType.TryGetValue(receiverType, out var enumType) && Enum.IsDefined(enumType, method)
				? enumType
				: null;
		}

		// The (Receiver.Type, method) pair -> "MethodName (0xHEX)". THE naming entry point, used by both directions of the
		// packet trace, so an RX line and a TX line name the same method the same way. The receiver Type already selects
		// the right class catalog, so nothing has to thread a typed method enum in from the listener.
		//
		// `fallbackEnum` covers the receiver classes the catalog doesn't map - notably the four login handoffs, which
		// address the ServiceConnection rather than either class in the map above, and are named from
		// PlayerInterfaceMethods. Pass it only for a direction whose class family the enum actually belongs to: an
		// index-registered method is a POSITION, so naming an inbound (c2s) method out of an s2c interface enum would
		// confidently print the WRONG method. That is why the RX side passes none and renders "Unknown".
		public static string ResolveName(in Rpc rpc, Type? fallbackEnum = null)
		{
			return ResolveName(rpc.Receiver.Type, rpc.Method, fallbackEnum);
		}

		public static string ResolveName(uint receiverType, uint method, Type? fallbackEnum = null)
		{
			var enumType = MethodEnumFor(receiverType, method) ?? Defining(fallbackEnum, method);

			return enumType is not null
				? $"{Enum.GetName(enumType, method)} ({method.ToHex()})"
				: $"Unknown ({method.ToHex()})";
		}

		// `fallbackEnum`, but only when it DEFINES the method. Every method enum is uint-backed since #615, so unlike
		// the old opcode/method split there is no backing-width mismatch left for Enum.IsDefined to throw on.
		private static Type? Defining(Type? fallbackEnum, uint method)
		{
			return fallbackEnum is not null && Enum.IsDefined(fallbackEnum, method)
				? fallbackEnum
				: null;
		}
	}
}
