using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network;

namespace Faolan.GameServer
{
	partial class GameServerListener
	{
		private static Dictionary<string, GmCommandDelegate> _gmCommandsDict;

		private void InitGmCommands()
		{
			_gmCommandsDict = new Dictionary<string, GmCommandDelegate>();
		}

		private async Task DoTeleport(INetworkClient client, uint mapId)
		{
			/*if (!arguments.TryParseNormalOrHex(out uint mapId))
			{
			    _agentServerListener?.SendSystemMessage(client.Account, $"Invalid input: '{arguments}'");
			    return;
			}*/

			var map = await Database.GetMap(mapId);
			if (map == null)
				//_agentServerListener?.SendSystemMessage(client.Account, $"Unknown mapId: '{arguments}'");
				return;

			client.Character.MapId = map.Id;
			//client.Character.Position = map.Position;
			client.Character.Rotation = map.Rotation;

			new PacketStream()
				.WriteHeader(Sender99, Receiver99, null, 0x2000)
				.WriteArrayUInt32Length(new ConanStream()
					.WriteUInt32(0x10d27bc0)
					.WriteUInt32(0x0000c350)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteUInt16(0x0162)
					.WriteUInt32(0x0000c79c)
					.WriteUInt32(client.Character.MapId ?? throw new Exception("client.Character.MapId == null"))
					.WriteUInt32(0x00000000)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteByte(0x00)
					.WriteUInt32(0x00000000)
					.WriteUInt32(0x00000000)
					.WriteUInt32(0x00000000))
				.Send(client);

			var pack20 = new byte[]
			{
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x62, 0x00, 0x00, 0xc7, 0x9c
			};

			var pack21 = new byte[]
			{
				0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x9c, 0x50, 0x00, 0x28, 0x3a, 0x0c, //instance
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x00, 0x01, 0x87, 0x04
			};

			new PacketStream()
				.WriteHeader(Sender99, Receiver99, null, 0x2000)
				.WriteArrayUInt32Length(new ConanStream()
					.WriteUInt32(0x5a32f0d7)
					.WriteUInt32(0x0000c350)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteArray(pack20)
					.WriteUInt32(client.Character.MapId ?? throw new Exception("client.Character.MapId == null"))
					.WriteUInt32(0)
					.WriteUInt32(client.Account.ClientInstance)
					.WriteArray(pack21)
					.WritePosition(client.Character)
					.WriteUInt32(0x00000032)
					.WriteByte(0))
				.Send(client);

			MapChange(client);
			SendReadyForPlayScreen(client);
			SpawnPlayer(client);
		}

		private void SendListAllMaps(Account account)
		{
			_agentServerListener.SendSystemMessage(account,
				string.Join("<br />", Database.Context.Maps.Select(m => $"{m.Id}: {m.Name}")));
		}

		private void SendListAllSpells(Account account)
		{
			_agentServerListener.SendSystemMessage(account,
				string.Join("<br />", Database.Context.Spells.Select(s => $"{s.Id}: {s.Name}")));
		}

		private void SendGmHelpText(Account account)
		{
			_agentServerListener.SendSystemMessage(account,
				$"Available Commands:<br />{string.Join("<br />", _gmCommandsDict.Keys)}");
		}

		private void ApplySpellTest(INetworkClient client, string arguments)
		{
			if (arguments.TryParseNormalOrHex(out uint id))
				ApplySpell(client, client.Account.ClientInstance, client.Account.ClientInstance, id);
		}

		private void HandleGmCommand(INetworkClient client, string commandText)
		{
			string command;
			string arguments = null;
			if (!commandText.Contains(' '))
				command = commandText.Substring(1).ToLower();
			else
			{
				var spaceIndex = commandText.IndexOf(' ');
				command = commandText.Substring(1, spaceIndex - 1).ToLower();
				arguments = commandText.Substring(spaceIndex + 1);
			}

			if (_gmCommandsDict.ContainsKey(command))
				_gmCommandsDict[command].Invoke(client, arguments);
			else
				_agentServerListener.SendSystemMessage(client.Account, $"Unknown command: '{command}' type .help for info");
		}

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
