using System;
using System.Collections.Generic;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Protocol
{
	// The single source of truth for turning a wire opcode into an interface-scoped enum/name. A 16-bit opcode is a
	// method INDEX, so the same number names a different method on each interface (0x2009 = DispatchGameServerStats on
	// GameInterface, DispatchChangeItemStackSize on GameCharInterface). The endpoint's Type (the interface-type hash)
	// disambiguates. Used by the packet loggers today, and by the structured serializer/parser as the pipeline rework
	// lands (Gitea #406).
	public static class ProtocolCatalog
	{
		// interface-type hash -> the *InterfaceOpcodes enum whose members are that interface's s2c methods.
		private static readonly IReadOnlyDictionary<uint, Type> InterfaceOpcodeEnums = new Dictionary<uint, Type>
		{
			[Endpoints.GameInterfaceType] = typeof(GameInterfaceOpcodes),
			[Endpoints.GameCharInterfaceType] = typeof(GameCharInterfaceOpcodes),
			[Endpoints.TradepostInterfaceType] = typeof(TradepostInterfaceOpcodes),
			[Endpoints.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceOpcodes)
		};

		// The opcode enum for a given interface hash, but only when that enum actually DEFINES the opcode; else null so
		// the caller falls back to its own default/expected enum. Both conditions matter: an interface we don't map, and
		// an opcode the mapped interface doesn't carry, are equally "not resolvable here".
		public static Type? OpcodeEnumFor(uint interfaceType, ushort opcode) =>
			InterfaceOpcodeEnums.TryGetValue(interfaceType, out var enumType) && Enum.IsDefined(enumType, opcode)
				? enumType
				: null;
	}
}
