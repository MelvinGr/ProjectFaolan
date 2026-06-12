using System;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.PacketAnalyzer
{
	public static class PacketProtocolInfo
	{
		public const string AgentServerProtocolName = "AgentServer";
		public const string CsPlayerAgentProtocolName = "CsPlayerAgent";
		public const string GameServerProtocolName = "GameServer";
		public const string PlayerAgentProtocolName = "PlayerAgent";
		public const string UniverseAgentProtocolName = "UniverseAgent";
		public const string UnknownProtocolName = "Unknown";

		private static readonly ProtocolOpcodeMap[] DisplayOpcodeMaps =
		[
			new ProtocolOpcodeMap(UniverseAgentProtocolName, typeof(UniverseAgentOpcodes), typeof(UniverseAgentResponseOpcodes)),
			new ProtocolOpcodeMap(PlayerAgentProtocolName, typeof(PlayerAgentOpcodes), typeof(PlayerAgentResponseOpcodes)),
			new ProtocolOpcodeMap(CsPlayerAgentProtocolName, typeof(CsPlayerAgentOpcodes), typeof(CsPlayerAgentResponseOpcodes)),
			new ProtocolOpcodeMap(AgentServerProtocolName, typeof(AgentServerOpcodes), typeof(AgentServerRespondsOpcodes)),
			new ProtocolOpcodeMap(GameServerProtocolName, typeof(GameServerOpcodes), typeof(GameServerRespondOpcodes))
		];

		private static readonly ProtocolOpcodeMap[] InferenceOpcodeMaps =
		[
			new ProtocolOpcodeMap(PlayerAgentProtocolName, typeof(PlayerAgentOpcodes), typeof(PlayerAgentResponseOpcodes)),
			new ProtocolOpcodeMap(CsPlayerAgentProtocolName, typeof(CsPlayerAgentOpcodes), typeof(CsPlayerAgentResponseOpcodes)),
			new ProtocolOpcodeMap(AgentServerProtocolName, typeof(AgentServerOpcodes), typeof(AgentServerRespondsOpcodes)),
			new ProtocolOpcodeMap(GameServerProtocolName, typeof(GameServerOpcodes), typeof(GameServerRespondOpcodes))
		];

		public static string GetDirectionName(bool isDownload)
		{
			return isDownload
				? "Download"
				: "Upload";
		}

		public static string GetOpcodeDisplay(string? protocolName, bool isDownload, ConanPacket packet)
		{
			var opcodeName = GetOpcodeName(protocolName, isDownload, packet);
			return opcodeName != null
				? $"{opcodeName} ({packet.Opcode.ToHex()})"
				: packet.Opcode.ToHex();
		}

		public static string GetOpcodeDisplay(string? protocolName, bool isDownload, uint opcode)
		{
			var opcodeName = GetOpcodeName(protocolName, isDownload, opcode);
			return opcodeName != null
				? $"{opcodeName} ({opcode.ToHex()})"
				: opcode.ToHex();
		}

		public static Type? GetOpcodeEnumType(string? protocolName, bool isDownload)
		{
			var map = FindProtocolOpcodeMap(DisplayOpcodeMaps, protocolName);
			return map?.GetEnumType(isDownload);
		}

		public static string? GetOpcodeName(string? protocolName, bool isDownload, ConanPacket packet)
		{
			var enumType = GetOpcodeEnumType(protocolName, isDownload);
			return GetEnumName(enumType, packet.Opcode);
		}

		public static string? GetOpcodeName(string? protocolName, bool isDownload, uint opcode)
		{
			var enumType = GetOpcodeEnumType(protocolName, isDownload);
			return GetEnumName(enumType, opcode);
		}

		public static string GetProtocolName(string? serviceName, bool isDownload, ConanPacket? packet = null)
		{
			if (packet != null && TryInferProtocolName(packet, isDownload, out var inferredProtocolName))
				return inferredProtocolName;

			return GetProtocolName(GetServicePort(serviceName), isDownload, packet?.Opcode);
		}

		public static string GetProtocolName(int? servicePort, bool isDownload, uint? opcode = null)
		{
			if (servicePort == PacketAnalyzerPortSettings.Default.UniverseAgentPort)
				return UniverseAgentProtocolName;

			if (opcode.HasValue && TryInferProtocolName(opcode.Value, isDownload, out var inferredProtocolName))
				return inferredProtocolName;

			return servicePort.HasValue
				? $"Port {servicePort.Value}"
				: UnknownProtocolName;
		}

		public static int? GetServicePort(string? serviceName)
		{
			if (string.IsNullOrWhiteSpace(serviceName))
				return null;

			var separatorIndex = serviceName.LastIndexOf(':');
			if (separatorIndex < 0)
				return null;

			return int.TryParse(serviceName[(separatorIndex + 1)..], out var port)
				? port
				: null;
		}

		public static bool TryGetSubOpcode(string? protocolName, bool isDownload, ConanPacket packet, out uint subOpcode, out string? subOpcodeName)
		{
			subOpcode = 0;
			subOpcodeName = null;

			if (!string.Equals(protocolName, GameServerProtocolName, StringComparison.OrdinalIgnoreCase) || packet.Opcode != (uint)GameServerOpcodes.PassBlob || packet.Body == null || packet.Body.Length < sizeof(uint) * 2)
				return false;

			var data = new FaolanStream(packet.Body.ToArray());
			_ = data.ReadUInt32();
			subOpcode = data.ReadUInt32();
			subOpcodeName = GetEnumName(isDownload
				? typeof(GameServer0X2000RespondsOpcodes)
				: typeof(GameServer0X2000Opcodes), subOpcode);
			return true;
		}

		public static bool TryInferProtocolName(ConanPacket packet, bool isDownload, out string protocolName)
		{
			if (packet.Opcode == (uint)GameServerOpcodes.PassBlob && packet.Body?.Length >= sizeof(uint) * 2)
			{
				var data = new FaolanStream(packet.Body.ToArray());
				_ = data.ReadUInt32();
				var subOpcode = data.ReadUInt32();
				if (GetEnumName(isDownload
					    ? typeof(GameServer0X2000RespondsOpcodes)
					    : typeof(GameServer0X2000Opcodes), subOpcode) != null)
				{
					protocolName = GameServerProtocolName;
					return true;
				}
			}

			return TryInferProtocolName(packet.Opcode, isDownload, out protocolName);
		}

		public static bool TryInferProtocolName(uint opcode, bool isDownload, out string protocolName)
		{
			foreach (var map in InferenceOpcodeMaps)
			{
				if (!IsDefined(map.GetEnumType(isDownload), opcode))
					continue;

				protocolName = map.ProtocolName;
				return true;
			}

			protocolName = UnknownProtocolName;
			return false;
		}

		private static ProtocolOpcodeMap? FindProtocolOpcodeMap(ProtocolOpcodeMap[] maps, string? protocolName)
		{
			foreach (var map in maps)
			{
				if (string.Equals(map.ProtocolName, protocolName, StringComparison.OrdinalIgnoreCase))
					return map;
			}

			return null;
		}

		private static string? GetEnumName(Type? enumType, uint value)
		{
			if (enumType == null || !IsDefined(enumType, value))
				return null;

			var enumValue = (Enum)Enum.ToObject(enumType, value);
			return enumValue.ToString();
		}

		private static bool IsDefined(Type? enumType, uint value)
		{
			if (enumType == null)
				return false;

			var underlyingType = Enum.GetUnderlyingType(enumType);
			var typedValue = underlyingType == typeof(byte) ? (object)(byte)value :
				underlyingType == typeof(sbyte) ? (sbyte)value :
				underlyingType == typeof(short) ? (short)value :
				underlyingType == typeof(ushort) ? (ushort)value :
				underlyingType == typeof(int) ? (int)value : value;

			return Enum.IsDefined(enumType, typedValue);
		}

		private readonly record struct ProtocolOpcodeMap(string ProtocolName, Type UploadEnumType, Type DownloadEnumType)
		{
			public Type GetEnumType(bool isDownload)
			{
				return isDownload
					? DownloadEnumType
					: UploadEnumType;
			}
		}
	}
}
