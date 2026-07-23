using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Server.GameServer
{
	public static class GameServerMethodNames
	{
		public static string Resolve(uint receiverClass, ushort opcode)
		{
			return receiverClass switch
			{
				Endpoints.GameAgentType => Name<GameAgentOpcodes>(opcode), // GameAgent
				Endpoints.GameCharAgentType => Name<GameCharAgentOpcodes>(opcode), // GameCharAgent
				Endpoints.GameCharAgentProjectType => Name<GameCharAgentProjectOpcodes>(opcode), // GameCharAgentProject
				_ => "Unknown"
			};
		}

		private static string Name<TEnum>(ushort opcode) where TEnum : struct, Enum
		{
			return Enum.IsDefined(typeof(TEnum), opcode)
				? ((TEnum)(object)opcode).ToString()
				: "Unknown";
		}
	}
}
