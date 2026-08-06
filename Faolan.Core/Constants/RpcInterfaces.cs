using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;

namespace Faolan.Core.Constants
{
	// The fourteen client RPC interfaces, each as one row: its client class NAME, its endpoint class ID, and the
	// `*Methods` enum that catalogs its methods.
	//
	// ⚠️ THIS IS THE ONE OWNER OF THE CLASS IDS. They had drifted into three places - `Endpoints`' constants (which
	// now forward here), the docs generator's `ExpectedClassIds`, and the importer's method census - and the
	// name/enum PAIRING into two. The pairing is the part that silently rots: an enum matched to the wrong class
	// names every frame on that interface wrongly, and nothing type-checks it.
	//
	// It lives in `Faolan.Core` because that is where its two inputs already are - the `*Methods` enums next door in
	// `Enums/`, and a class id is a plain constant. `Faolan.Core.Network.Endpoints` builds the wire `ProtoEndpoint`
	// from these, which is the network concern; naming them is not.
	//
	// ⚠️ A `RpcInterface.Type` is `Crypt.Bkdr31.Of(Name)` - see that type for the hash and the other id namespace it
	// serves. That is ASSERTED, not assumed: the docs generator
	// recomputes every one and fails the build on a mismatch, which catches a typo'd or renamed class before it
	// silently mismatches the Wireshark dissector.
	public static class RpcInterfaces
	{
		public const uint UniverseInterfaceType = 0x0C384054;
		public const uint GameCharInterfaceProjectType = 0x28377648; // the report-server-id family
		public const uint TradepostAgentType = 0x3D744981; // tradepost (AH + mail), client -> server
		public const uint GameCharAgentProjectType = 0x5B9756FC; // the post-ready family
		public const uint UniverseAgentType = 0x604DDBA0;
		public const uint GameInterfaceType = 0x6C67C147;
		public const uint CSPlayerAgentType = 0x6DD55614; // the CsPlayerAgent reply endpoint
		public const uint PlayerInterfaceType = 0x7D155738;
		public const uint PlayerAgentType = 0x82F20484;
		public const uint GameCharInterfaceType = 0x8B87F791; // the 0x201C spawn-handshake endpoint
		public const uint GameCharAgentType = 0xA9ECB95D; // the 0x201C spawn-handshake caller
		public const uint GameAgentType = 0xB171CE13;
		public const uint CSPlayerInterfaceType = 0xBBD560C8; // the CsPlayerAgent reply caller
		public const uint TradepostInterfaceType = 0xF2C2D5B5; // tradepost, server -> client

		// Not an RPC interface with a method catalog of its own - the login tier's service-connection class - so it
		// is a bare id here rather than a row in `All`.
		public const uint PlayerAgentServiceConnectionType = 0x1D359116;

		// c2s receivers are the *Agent classes, s2c receivers the *Interface ones - so the RpcHeader's RECEIVER class
		// alone picks the table that names a frame.
		public static readonly RpcInterface[] All =
		[
			new RpcInterface("UniverseAgent", UniverseAgentType, typeof(UniverseAgentMethods)),
			new RpcInterface("UniverseInterface", UniverseInterfaceType, typeof(UniverseInterfaceMethods)),
			new RpcInterface("PlayerAgent", PlayerAgentType, typeof(PlayerAgentMethods)),
			new RpcInterface("PlayerInterface", PlayerInterfaceType, typeof(PlayerInterfaceMethods)),
			new RpcInterface("CSPlayerAgent", CSPlayerAgentType, typeof(CSPlayerAgentMethods)),
			new RpcInterface("CSPlayerInterface", CSPlayerInterfaceType, typeof(CSPlayerInterfaceMethods)),
			new RpcInterface("GameAgent", GameAgentType, typeof(GameAgentMethods)),
			new RpcInterface("GameInterface", GameInterfaceType, typeof(GameInterfaceMethods)),
			new RpcInterface("GameCharAgent", GameCharAgentType, typeof(GameCharAgentMethods)),
			new RpcInterface("GameCharInterface", GameCharInterfaceType, typeof(GameCharInterfaceMethods)),
			new RpcInterface("GameCharAgentProject", GameCharAgentProjectType, typeof(GameCharAgentProjectMethods)),
			new RpcInterface("GameCharInterfaceProject", GameCharInterfaceProjectType, typeof(GameCharInterfaceProjectMethods)),
			new RpcInterface("TradepostAgent", TradepostAgentType, typeof(TradepostAgentMethods)),
			new RpcInterface("TradepostInterface", TradepostInterfaceType, typeof(TradepostInterfaceMethods))
		];

		// The interface an endpoint class id names, or null for one outside the registry - a capture can carry a
		// class we have not catalogued (PlayerAgentServiceConnection, for one).
		public static RpcInterface? ByType(uint type)
		{
			return All.FirstOrDefault(i => i.Type == type);
		}

		public static RpcInterface? ByEnum(Type methods)
		{
			return All.FirstOrDefault(i => i.Methods == methods);
		}

	}

	// `Methods` is the `*Methods` enum TYPE; `Members` walks it. A `Type` rather than a generic parameter because
	// the fourteen rows have to live in one array.
	public sealed record RpcInterface(string Name, uint Type, Type Methods)
	{
		public IEnumerable<Enum> Members => Enum.GetValues(Methods).Cast<Enum>();
	}
}
