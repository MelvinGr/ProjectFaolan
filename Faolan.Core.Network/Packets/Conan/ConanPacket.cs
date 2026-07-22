using System;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Packets.Conan
{
	public class ConanPacket : Packet
	{
		public readonly byte[] HeaderData; // Remaining data

		// Typed header endpoints, read straight off the header stream (ProtoEndpoint.Read). Sender = RpcHeader.caller_id
		// (field 1), Receiver = RpcHeader.endpoint_id (field 2); each carries Type (object-class id) + Instance (the
		// addressed object). See Endpoints. Echo them back with WriteHeader(receiver, sender, ...) for a reply.
		public readonly ProtoEndpoint Receiver;
		public readonly ProtoEndpoint Sender;

		public readonly uint Crc32;
		public readonly ushort Opcode;

		public ConanPacket(byte[] bytes) : base(bytes)
		{
			Length = Stream.ReadUInt32();
			Crc32 = Stream.ReadUInt32();

			IsValid = Crc32 == Crypt.Crc32.Calculate(Stream);
			// if (!IsValid) Console.WriteLine($"Invalid {GetType().Name}");

			using var header = new FaolanStream(Stream.ReadArrayUInt32Length());
			Sender = ProtoEndpoint.Read(header);
			Receiver = ProtoEndpoint.Read(header);
			Opcode = header.ReadUInt16();
			HeaderData = header.ReadRemainingArray();

			Body = new FaolanStream(Stream.ReadRemainingArray());
		}

		public override string ToString()
		{
			return $"Opcode: {Opcode.ToHex()} Length: {Length}{(Body != null ? Environment.NewLine + Body.ToArray().ToHex() : "")}";
		}
	}
}
