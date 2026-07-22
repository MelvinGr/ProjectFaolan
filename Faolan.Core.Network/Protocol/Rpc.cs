using System;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Protocol
{
	// The structured form of a Conan RPC frame - what a handler builds and hands to the transport, and what the parser
	// yields for an inbound frame - instead of a raw byte[] that has to be re-parsed to be logged or inspected.
	// Serializing this to the wire ([u32 length][u32 crc32][u32 headerLen][sender][receiver][u16 opcode][headerData][body])
	// and parsing it back are inverses (the contract PacketFrameRoundTripTests locks). Introduced for the
	// structured-packet-pipeline epic (Gitea #406, phase 1); NOT yet wired into the send path.
	//
	// Sender = RpcHeader.caller_id; Receiver = the addressed object - its Type is the interface hash, which names the
	// opcode against it (see ProtocolCatalog). HeaderData is the interface-specific header tail (e.g. the PlayerInterface
	// 32-bit method id, or a keepalive's 1-byte flag); Body is everything after the header.
	public readonly record struct Rpc(
		ProtoEndpoint Sender,
		ProtoEndpoint Receiver,
		ushort Opcode,
		ReadOnlyMemory<byte> HeaderData,
		ReadOnlyMemory<byte> Body);
}
