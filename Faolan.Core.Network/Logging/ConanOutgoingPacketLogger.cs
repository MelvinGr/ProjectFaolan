using System;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Core.Network.Protocol;
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
			var opcode = packet.Opcode;

			// Name the opcode from the receiver's interface enum when the catalog resolves it; else fall back to the
			// listener's default enum (single-interface listeners, or an opcode the mapped interface doesn't carry).
			var opcodeEnumType = (packet.Receiver is { } receiver ? ProtocolCatalog.OpcodeEnumFor(receiver.Type, opcode) : null) ?? _opcodeEnumType;
			var opcodeName = PacketLogFormatter.OpcodeName(opcodeEnumType, opcode);

			if (_subOpcodeEnumType != null && opcode == (ushort)GameAgentOpcodes.PassBlob && packet.Body?.Length >= sizeof(uint) * 2)
			{
				var data = new FaolanStream(packet.Body.ToArray());
				var payloadLength = data.ReadUInt32();
				var subOpcodeValue = data.ReadUInt32();

				Logger.LogPacketWithSubOpcode(PacketLogFormatter.Downlink, ChannelName, opcodeName, PacketLogFormatter.OpcodeName(_subOpcodeEnumType, subOpcodeValue), payloadLength,
					packet.Body.Length, packet.Bytes.Length, ("headerData", packet.HeaderData), ("body", packet.Body.ToArray()));

				return;
			}

			Logger.LogPacket(PacketLogFormatter.Downlink, ChannelName, opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length, ("headerData", packet.HeaderData), ("body", packet.Body?.ToArray()));
		}
	}
}
