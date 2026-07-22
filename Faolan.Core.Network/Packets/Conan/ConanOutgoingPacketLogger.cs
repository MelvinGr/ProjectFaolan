using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Interfaces;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Packets.Conan
{
	public sealed class ConanOutgoingPacketLogger : IPacketLogger
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

		private readonly ILogger _logger;
		private readonly string _channelName;
		private readonly Type _opcodeEnumType;
		private readonly Type? _subOpcodeEnumType;

		public ConanOutgoingPacketLogger(ILogger logger, string channelName, Type opcodeEnumType, Type? subOpcodeEnumType = null)
		{
			_logger = logger;
			_channelName = channelName;
			_opcodeEnumType = opcodeEnumType;
			_subOpcodeEnumType = subOpcodeEnumType;
		}

		public void Log(byte[] bytes)
		{
			try
			{
				var packet = new ConanPacket(bytes);
				var opcode = packet.Opcode;

				// Name the opcode from the receiver's interface enum when it defines it; else fall back to the
				// listener's default enum (single-interface listeners, or an opcode the mapped interface doesn't carry).
				var opcodeEnumType = packet.Receiver is { } receiver && InterfaceOpcodeEnums.TryGetValue(receiver.Type, out var byInterface) && Enum.IsDefined(byInterface, opcode)
					? byInterface
					: _opcodeEnumType;
				var opcodeName = FormatOpcodeName(opcodeEnumType, opcode);

				if (_subOpcodeEnumType != null && opcode == (ushort)GameAgentOpcodes.PassBlob && packet.Body?.Length >= sizeof(uint) * 2)
				{
					var data = new FaolanStream(packet.Body.ToArray());
					var payloadLength = data.ReadUInt32();
					var subOpcodeValue = data.ReadUInt32();
					var subOpcodeName = FormatSubOpcodeName(_subOpcodeEnumType, subOpcodeValue);

					_logger.LogInformation("DL [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}",
						_channelName, opcodeName, subOpcodeName, payloadLength, packet.Body.Length, packet.Bytes.Length, BytesSuffix(packet));

					return;
				}

				_logger.LogInformation("DL [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", _channelName, opcodeName, packet.Body?.Length ?? 0,
					packet.Bytes.Length, BytesSuffix(packet));
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing {Channel} Conan packet for logging.", _channelName);
			}
		}

		private string BytesSuffix(ConanPacket packet)
		{
			return PacketLogOptions.BytesSuffix(_logger, ("headerData", packet.HeaderData), ("body", packet.Body?.ToArray()));
		}

		private static string FormatOpcodeName(Type enumType, ushort opcode)
		{
			return Enum.IsDefined(enumType, opcode)
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: opcode.ToHex();
		}

		private static string FormatSubOpcodeName(Type enumType, uint opcode)
		{
			return Enum.IsDefined(enumType, opcode)
				? $"{Enum.GetName(enumType, opcode)} ({opcode.ToHex()})"
				: opcode.ToHex();
		}
	}
}
