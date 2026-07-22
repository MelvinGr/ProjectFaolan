using System;
using System.Collections.Generic;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Logging
{
	public sealed class ConanOutgoingPacketLogger : OutgoingPacketLogger
	{
		// Outgoing (s2c) opcodes are named per INTERFACE: a 16-bit opcode is a method INDEX, so the same number is a
		// different method on each interface (0x2009 is DispatchGameServerStats on GameInterface but
		// DispatchChangeItemStackSize on GameCharInterface). The packet's receiver endpoint carries which interface it
		// targets (ProtoEndpoint.Type = the interface-type hash), so resolve the enum from THAT rather than a single
		// fixed enum - otherwise a reply on one interface is labeled with another interface's method name. This is a
		// global protocol fact (interface -> its opcode enum), so it corrects every listener's outgoing names at once.
		private static readonly IReadOnlyDictionary<uint, Type> InterfaceOpcodeEnums = new Dictionary<uint, Type>
		{
			[Endpoints.GameInterfaceType] = typeof(GameInterfaceOpcodes),
			[Endpoints.GameCharInterfaceType] = typeof(GameCharInterfaceOpcodes),
			[Endpoints.TradepostInterfaceType] = typeof(TradepostInterfaceOpcodes),
			[Endpoints.CSPlayerInterfaceType] = typeof(CSPlayerInterfaceOpcodes)
		};

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

			// Name the opcode from the receiver's interface enum when it defines it; else fall back to the listener's
			// default enum (single-interface listeners, or an opcode the mapped interface doesn't carry).
			var opcodeEnumType = packet.Receiver is { } receiver && InterfaceOpcodeEnums.TryGetValue(receiver.Type, out var byInterface) && Enum.IsDefined(byInterface, opcode)
				? byInterface
				: _opcodeEnumType;
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
