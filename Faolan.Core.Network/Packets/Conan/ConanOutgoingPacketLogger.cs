using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Interfaces;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Packets.Conan
{
	public sealed class ConanOutgoingPacketLogger : IPacketLogger
	{
		private readonly ILogger _logger;
		private readonly string _channelName;
		private readonly Type _opcodeEnumType;
		private readonly Type _subOpcodeEnumType;

		public ConanOutgoingPacketLogger(ILogger logger, string channelName, Type opcodeEnumType, Type subOpcodeEnumType = null)
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
				var opcodeName = FormatOpcodeName(_opcodeEnumType, opcode);

				if (_subOpcodeEnumType != null && opcode == (ushort)GameAgentOpcodes.PassBlob && packet.Body?.Length >= sizeof(uint) * 2)
				{
					var data = new FaolanStream(packet.Body.ToArray());
					var payloadLength = data.ReadUInt32();
					var subOpcodeValue = data.ReadUInt32();
					var subOpcodeName = FormatSubOpcodeName(_subOpcodeEnumType, subOpcodeValue);

					_logger.LogInformation("<= [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", _channelName,
						opcodeName, subOpcodeName, payloadLength, packet.Body.Length, packet.Bytes.Length, BytesSuffix(packet));

					return;
				}

				_logger.LogInformation("<= [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}{Bytes}", _channelName, opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length, BytesSuffix(packet));
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing {Channel} Conan packet for logging.", _channelName);
			}
		}

		private string BytesSuffix(ConanPacket packet) =>
			PacketLogOptions.BytesSuffix(_logger, ("headerData", packet.HeaderData), ("body", packet.Body?.ToArray()));

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
