namespace Faolan.Core.Network.Constants
{
	// RPC opcodes that are not a member of any one interface's opcode enum, because they mean different things
	// depending on which conversation they arrive on.
	//
	// A 16-bit RPC opcode is a METHOD INDEX: `wire = 0x2000 + registration index`, with base 0x2000 for every
	// interface (docs/re/aoc-rpc-method-opcodes.md). So 0x2000 is simply "method 0", and every interface has one -
	// which is why `Faolan.Core.Enums.Opcodes` defines it a dozen times over under a dozen names (Authenticate,
	// PassBlob, RequestAggroStatus, GetAllMails, DispatchChallenge, ...). Those enum members are the right way to
	// name it when you know the interface.
	//
	// IirCarrier is for when you do NOT: a capture scan that filters packets by opcode alone, before it knows or
	// cares which endpoint they belong to. Fourteen importer extractors were each declaring their own, under five
	// names (`Opcode2000` x9, `IirCarrierOpcode` x2, `CarrierOpcode`, `WorldOpcode`).
	public static class RpcOpcode
	{
		// Method 0 on the GameChar conversation, which is the 0x2000 IIR carrier - the packet whose body is a
		// length-prefixed IIR record rather than a typed RPC argument list. Named for what a capture scan is looking
		// for; when the interface is known, prefer that interface's own enum member.
		public const uint IirCarrier = 0x2000;
	}
}
