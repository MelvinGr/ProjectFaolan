using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Interfaces;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Network.Conan
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

				if (_subOpcodeEnumType != null && opcode == (ushort)GameServerOpcodes.PassBlob && packet.Body?.Length >= sizeof(uint) * 2)
				{
					var data = new FaolanStream(packet.Body.ToArray());
					var payloadLength = data.ReadUInt32();
					var subOpcodeValue = data.ReadUInt32();
					var subOpcodeName = FormatSubOpcodeName(_subOpcodeEnumType, subOpcodeValue);

					_logger.LogInformation("<= [{Channel} {OpcodeName}] subOpcode={SubOpcodeName}, payloadLength={PayloadLength}, bodyLength={BodyLength}, packetLength={PacketLength}",
						_channelName,
						opcodeName,
						subOpcodeName,
						payloadLength,
						packet.Body.Length,
						packet.Bytes.Length);
					LogDebugDetails(opcodeName, packet);

					if (subOpcodeValue == (uint)GameServer0X2000RespondsOpcodes.StatIIR && data.Length >= 25)
					{
						var unk0 = data.ReadUInt32();
						var characterId = data.ReadUInt32();
						var flag = data.ReadByte();
						var value0 = data.ReadUInt32();
						var value1 = data.ReadUInt32();
						_logger.LogInformation("<= [{Channel} {OpcodeName}] 0xF98E10B3 unk0={Unk0:X}, characterId={CharacterId:X}, flag={Flag:X2}, value0={Value0:X}, value1={Value1:X}",
							_channelName,
							opcodeName,
							unk0,
							characterId,
							flag,
							value0,
							value1);
					}

					return;
				}

				_logger.LogInformation("<= [{Channel} {OpcodeName}] bodyLength={BodyLength}, packetLength={PacketLength}", _channelName, opcodeName, packet.Body?.Length ?? 0, packet.Bytes.Length);
				LogDebugDetails(opcodeName, packet);

				if (opcode == (ushort)GameCharInterfaceDispatchOpcodes.DispatchSetQuestMarker && packet.Body?.Length >= sizeof(uint) * 2 + sizeof(byte))
				{
					var data = new FaolanStream(packet.Body.ToArray());
					var unk0 = data.ReadUInt32();
					var requestId = data.ReadUInt32();
					var flag = data.ReadByte();
					_logger.LogInformation("<= [{Channel} {OpcodeName}] unk0={Unk0:X}, requestId={RequestId:X}, flag={Flag:X2}", _channelName, opcodeName, unk0, requestId, flag);
				}
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to decode outgoing {Channel} Conan packet for logging.", _channelName);
			}
		}

		private void LogDebugDetails(string opcodeName, ConanPacket packet)
		{
			if (!_logger.IsEnabled(LogLevel.Debug))
				return;

			if (packet.HeaderData?.Length > 0)
				_logger.LogDebug("<= [{Channel} {OpcodeName}] headerData ({HeaderDataLength} bytes): {HeaderData}", _channelName, opcodeName, packet.HeaderData.Length, packet.HeaderData.ToHex());

			if (packet.Body?.Length > 0)
			{
				_logger.LogDebug("<= [{Channel} {OpcodeName}] body ({BodyLength} bytes): {Body}", _channelName, opcodeName, packet.Body.Length, packet.Body.ToArray().ToHex());
			}
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
