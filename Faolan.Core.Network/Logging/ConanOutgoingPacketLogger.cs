using System;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	public sealed class ConanOutgoingPacketLogger : OutgoingPacketLogger
	{
		private readonly Type _opcodeEnumType;
		private readonly Type? _subOpcodeEnumType;

		public ConanOutgoingPacketLogger(ILogger logger, string channelName, Type opcodeEnumType, Type? subOpcodeEnumType = null) : base(logger, channelName)
		{
			_opcodeEnumType = opcodeEnumType;
			_subOpcodeEnumType = subOpcodeEnumType;
		}

		protected override void LogPacket(byte[] bytes)
		{
			var packet = new ConanPacket(bytes);
			Render(packet.Opcode, packet.Receiver, packet.HeaderData, packet.Body?.ToArray(), packet.Bytes.Length);
		}

		// Phase 6: log a sent Rpc from its own fields - the model already carries the Opcode, Receiver (which names the
		// opcode's interface) and the HeaderData/Body the trace needs, so there is no reason to serialize-then-re-parse.
		// `wireBytes` is the already-serialized frame, used only for the total wire length the byte[] path read off
		// ConanPacket.Bytes.Length.
		protected override void LogPacket(in Rpc rpc, byte[] wireBytes)
		{
			Render(rpc.Opcode, rpc.Receiver, rpc.HeaderData.ToArray(), rpc.Body.ToArray(), wireBytes.Length);
		}

		// The shared render used by both paths (re-parsed bytes and the structured Rpc). Names the opcode from the
		// receiver's interface enum when the catalog resolves it; else falls back to the listener's default enum
		// (single-interface listeners, or an opcode the mapped interface doesn't carry). A PassBlob carrier additionally
		// names its inner IIR sub-opcode.
		private void Render(ushort opcode, ProtoEndpoint? receiver, byte[] headerData, byte[]? body, int wireLength)
		{
			var opcodeEnumType = (receiver is { } r ? ProtocolCatalog.OpcodeEnumFor(r.Type, opcode) : null) ?? _opcodeEnumType;
			var opcodeName = PacketLogFormatter.OpcodeName(opcodeEnumType, opcode);

			if (_subOpcodeEnumType != null && opcode == (ushort)GameAgentOpcodes.PassBlob && body?.Length >= sizeof(uint) * 2)
			{
				var data = new FaolanStream(body);
				var payloadLength = data.ReadUInt32();
				var subOpcodeValue = data.ReadUInt32();

				Logger.LogPacketWithSubOpcode(PacketLogFormatter.Downlink, ChannelName, opcodeName, PacketLogFormatter.OpcodeName(_subOpcodeEnumType, subOpcodeValue), payloadLength,
					body.Length, wireLength, ("headerData", headerData), ("body", body));

				return;
			}

			Logger.LogPacket(PacketLogFormatter.Downlink, ChannelName, opcodeName, body?.Length ?? 0, wireLength, ("headerData", headerData), ("body", body));
		}
	}
}
