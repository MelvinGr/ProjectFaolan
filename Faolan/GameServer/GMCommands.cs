using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Data;
using Faolan.Core.Extentions;
using Faolan.Core.Network;

namespace Faolan.GameServer
{
    partial class GameServerListener
    {
        private static Dictionary<string, GmCommandDelegate> _gmCommandsDict;

        private void InitGmCommands()
        {
            _gmCommandsDict = new Dictionary<string, GmCommandDelegate>
            {
                {"help", (c, arg) => SendGmHelpText(c.Account)},
                {"broadcast", (c, arg) => _agentServerListener.SystemBroadcast(arg)},
                {"listmaps", (c, arg) => SendListAllMaps(c.Account)},
                {"listspells", (c, arg) => SendListAllSpells(c.Account)},
                {"teleport", (c, arg) => DoTeleport(c, arg)},
                {"savedata", (c, arg) => SaveData()},
                {"settimeofday", (c, arg) => Packets.Packets.SetTimeofDay(c)},
                {
                    "printpos",
                    (c, arg) =>
                        _agentServerListener.SendSystemMessage(c.Account, c.Account.Character.Position.ToString())
                },
                {"_mount", (c, arg) => SendSitOnMountTest(c)},
                {"_buff", (c, arg) => SendPlayerBuffsTest(c)},
                {"_casteffect", (c, arg) => ApplySpellTest(c, arg)},
                {"_spawnrunningchild", (c, arg) => Spawnrunningchild(c)}
            };
        }

        private void SaveData()
        {
            SaveAllCharacterData();
        }

        private static void DoTeleport(INetworkClient client, string arguments)
        {
            uint mapId;
            if (!arguments.TryParseNormalOrHex(out mapId))
            {
                _agentServerListener.SendSystemMessage(client.Account, "Invalid input: '" + arguments + "'");
                return;
            }

            var map = ConanMap.AllMaps.FirstOrDefault(m => m.Id == mapId);
            if (map == null)
            {
                _agentServerListener.SendSystemMessage(client.Account, "Unknown mapId: '" + arguments + "'");
                return;
            }

            client.Character.Map = map.Id;
            //client.Character.Position = map.Position;
            client.Character.Rotation = map.Rotation;
            client.Character.SaveDataToDatabase(_databaseStatic);

            new PacketStream()
                .WriteHeader(Sender99, Receiver99, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x10d27bc0)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteUInt16(0x0162)
                    .WriteUInt32(0x0000c79c)
                    .WriteUInt32(client.Character.Map)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteByte(0x00)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(0x00000000))
                .Send(client);

            var pack20 = new byte[]
            {
                0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x3f, 0x80, 0x00, 0x00,
                0x62,
                0x00, 0x00, 0xc7, 0x9c
            };

            var pack21 = new byte[]
            {
                0x00, 0x00, 0x00, 0x0f,
                0x00, 0x00, 0x9c, 0x50,
                0x00, 0x28, 0x3a, 0x0c, //instance
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x14, 0x00, 0x01, 0x87, 0x04
            };

            new PacketStream()
                .WriteHeader(Sender99, Receiver99, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x5a32f0d7)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteArray(pack20)
                    .WriteUInt32(client.Character.Map)
                    .WriteUInt32(0)
                    .WriteUInt32(client.Account.ClientInstance)
                    .WriteArray(pack21)
                    .WriteVector3(client.Character.Position)
                    .WriteUInt32(0x00000032)
                    .WriteByte(0))
                .Send(client);

            Packets.Packets.MapChange(client);
            Packets.Packets.SendReadyForPlayScreen(client);
            Packets.Packets.SpawnPlayer(client);
        }

        private static void SendListAllMaps(Account account)
        {
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", ConanMap.AllMaps.Select(m => m.Id + ": " + m.Name)));
        }

        private static void SendListAllSpells(Account account)
        {
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", Spell.AllSpells.Select(s => s.Id + ": " + s.Name)));
        }

        private static void SendGmHelpText(Account account)
        {
            _agentServerListener.SendSystemMessage(account,
                "Available Commands:<br />" + string.Join("<br />", _gmCommandsDict.Keys));
        }

        private static void ApplySpellTest(INetworkClient client, string arguments)
        {
            uint id;
            if (arguments.TryParseNormalOrHex(out id))
                Packets.Packets.ApplySpell(client, client.Account.ClientInstance,
                    client.Account.ClientInstance, id);
        }

        private void HandleGmCommand(INetworkClient client, string commandText)
        {
            string command;
            string arguments = null;
            if (!commandText.Contains(' '))
            {
                command = commandText.Substring(1).ToLower();
            }
            else
            {
                var spaceIndex = commandText.IndexOf(' ');
                command = commandText.Substring(1, spaceIndex - 1).ToLower();
                arguments = commandText.Substring(spaceIndex + 1);
            }

            if (_gmCommandsDict.ContainsKey(command))
                _gmCommandsDict[command].Invoke(client, arguments);
            else
                _agentServerListener.SendSystemMessage(client.Account, "Unknown command: '" +
                                                                       command + "' type .help for info");
        }

        private delegate void GmCommandDelegate(INetworkClient client, string arguments);
    }
}