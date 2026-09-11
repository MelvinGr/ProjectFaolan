using System;
using System.Collections.Generic;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Protocol
{
	/// <summary>
	///     The single source of truth for turning a wire method into an interface-scoped enum or name.
	/// </summary>
	/// <remarks>
	///     On the index-registered classes a method is a registration index, so the same number names a different method
	///     on each interface or agent (0x00 = InitiateAuthentication on UniverseAgent, Authenticate on CsPlayerAgent;
	///     0x09 = DispatchGameServerStats on GameInterface, DispatchChangeItemStackSize on GameCharInterface). The
	///     endpoint's Type - the interface/agent-type hash - disambiguates, so the (Receiver.Type, method) pair uniquely
	///     identifies the method.
	///     <para>
	///         Used by both packet loggers: inbound receivers are the <c>*Agent</c> endpoints (c2s), outbound receivers
	///         are the <c>*Interface</c> endpoints (s2c).
	///     </para>
	/// </remarks>
	public static class ProtocolCatalog
	{
		// (interface/agent-type hash) -> the *Methods enum whose members are that endpoint's methods. Covers the c2s
		// *Agent classes (inbound naming) and the s2c *Interface classes (outbound naming). A method index never
		// collides across this map because the receiver Type keys it.
		//
		// The hash-registered PlayerAgent/PlayerInterface pair sits in the same map: every member is its own
		// `method_instance` constant, indices are <= 0xD2 and hashes are 32-bit, so one map and one lookup serve both.
		private static readonly IReadOnlyDictionary<uint, Type> MethodEnumsByReceiverType = new Dictionary<uint, Type>
		{
			// c2s: the client addresses the server's *Agent endpoint (the inbound UL trace).
			[RpcInterfaces.UniverseAgentType] = typeof(UniverseAgentMethods),
			[RpcInterfaces.CSPlayerAgentType] = typeof(CSPlayerAgentMethods),
			[RpcInterfaces.GameAgentType] = typeof(GameAgentMethods),
			[RpcInterfaces.GameCharAgentType] = typeof(GameCharAgentMethods),
			[RpcInterfaces.GameCharAgentProjectType] = typeof(GameCharAgentProjectMethods),
			[RpcInterfaces.TradepostAgentType] = typeof(TradepostAgentMethods),
			[RpcInterfaces.PlayerAgentType] = typeof(PlayerAgentMethods),

			// s2c: the server addresses the client's *Interface endpoint (the outbound DL trace).
			[RpcInterfaces.GameInterfaceType] = typeof(GameInterfaceMethods),
			[RpcInterfaces.GameCharInterfaceType] = typeof(GameCharInterfaceMethods),
			[RpcInterfaces.GameCharInterfaceProjectType] = typeof(GameCharInterfaceProjectMethods),
			[RpcInterfaces.TradepostInterfaceType] = typeof(TradepostInterfaceMethods),
			[RpcInterfaces.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceMethods),
			[RpcInterfaces.PlayerInterfaceType] = typeof(PlayerInterfaceMethods)
		};

		/// <summary>
		///     The method enum for a given receiver-type hash, but only when that enum defines the method; otherwise
		///     null, so the caller falls back to its own expected enum.
		/// </summary>
		/// <remarks>
		///     Both conditions matter: an unmapped receiver type and a method the mapped enum does not carry are equally
		///     "not resolvable here".
		/// </remarks>
		public static Type? MethodEnumFor(uint receiverType, uint method)
		{
			return MethodEnumsByReceiverType.TryGetValue(receiverType, out var enumType) && Enum.IsDefined(enumType, method)
				? enumType
				: null;
		}

		/// <summary>
		///     The (Receiver.Type, method) pair rendered as "MethodName (0xHEX)" - the naming entry point for both
		///     directions of the packet trace, so an RX line and a TX line name the same method the same way.
		/// </summary>
		/// <param name="fallbackEnum">
		///     Covers the receiver classes the catalog does not map, notably the four login handoffs, which address the
		///     ServiceConnection and are named from <c>PlayerInterfaceMethods</c>. Pass it only for a direction whose
		///     class family the enum belongs to: an index-registered method is a position, so naming an inbound method
		///     out of an s2c interface enum would confidently print the wrong method. The inbound side passes none and
		///     renders "Unknown".
		/// </param>
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

		// `fallbackEnum`, but only when it defines the method. Every method enum is uint-backed, so there is no
		// backing-width mismatch for Enum.IsDefined to throw on.
		private static Type? Defining(Type? fallbackEnum, uint method)
		{
			return fallbackEnum is not null && Enum.IsDefined(fallbackEnum, method)
				? fallbackEnum
				: null;
		}
	}
}
