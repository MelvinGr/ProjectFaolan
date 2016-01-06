using System.Collections.Generic;
using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private static Dictionary<string, GmCommandDelegate> _gmCommandsDict;

        private void InitGmCommands()
        {
            _gmCommandsDict = new Dictionary<string, GmCommandDelegate>
            {
                {"help", (c, a, arg) => SendGmHelpText(a)},
                {"broadcast", (c, a, arg) => _agentServerListener.SystemBroadcast(arg)},
                {"listmaps", (c, a, arg) => SendListAllMaps(a)},
                {"listspells", (c, a, arg) => SendListAllSpells(a)},
                {"teleport", (c, a, arg) => DoTeleport(c, a, arg)},
                {"savedata", (c, a, arg) => SaveData()},
                {"settimeofday", (c, a, arg) => SetTimeofDay(c)},
                {"printpos", (c, a, arg) => _agentServerListener.SendSystemMessage(a, a.Character.Position.ToString())},
                {"_mount", (c, a, arg) => SendSitOnMountTest(c, a)},
                {"_buff", (c, a, arg) => SendPlayerBuffsTest(c, a)},
                {"_casteffect", (c, a, arg) => ApplySpellTest(c, a, arg)},
                {"_spawnrunningchild", (c, a, arg) => Spawnrunningchild(c, a)}
            };
        }

        private void SaveData()
        {
            SaveAllCharacterData();
        }

        private static void DoTeleport(NetworkClient client, Account account, string arguments)
        {
            uint mapId;
            if (!arguments.TryParseNormalOrHex(out mapId))
            {
                _agentServerListener.SendSystemMessage(account, "Invalid input: '" + arguments + "'");
                return;
            }

            var map = ConanMap.AllMaps.FirstOrDefault(m => m.Id == mapId);
            if (map == null)
            {
                _agentServerListener.SendSystemMessage(account, "Unknown mapId: '" + arguments + "'");
                return;
            }

            account.Character.Map = map.Id;
            account.Character.Position = map.Position;
            account.Character.Rotation = map.Rotation;
            account.Character.SaveDataToDatabase(Database);

            new PacketStream()
                .WriteHeader(Sender99, Receiver99, null, 0x2000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(0x10d27bc0)
                    .WriteUInt32(0x0000c350)
                    .WriteUInt32(account.ClientInstance)
                    .WriteUInt16(0x0162)
                    .WriteUInt32(0x0000c79c)
                    .WriteUInt32(account.Character.Map)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(account.ClientInstance)
                    .WriteByte(0x00)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(0x00000000)
                    .WriteUInt32(0x00000000))
                .Send(client);

            var pack2_0 = new byte[]
            {
                0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x3f, 0x80, 0x00, 0x00,
                0x62,
                0x00, 0x00, 0xc7, 0x9c
            };

            var pack2_1 = new byte[]
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
                    .WriteUInt32(account.ClientInstance)
                    .WriteArray(pack2_0)
                    .WriteUInt32(account.Character.Map)
                    .WriteUInt32(0)
                    .WriteUInt32(account.ClientInstance)
                    .WriteArray(pack2_1)
                    .WriteVector3(account.Character.Position)
                    .WriteUInt32(0x00000032)
                    .WriteByte(0))
                .Send(client);

            MapChange(client, account);
            SendReadyForPlayScreen(client, account);
            SpawnPlayer(client, account);
        }

        private static void SendListAllMaps(Account account) =>
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", ConanMap.AllMaps.Select(m => m.Id + ": " + m.Name)));

        private static void SendListAllSpells(Account account) =>
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", Spell.AllSpells.Select(s => s.Id + ": " + s.Name)));

        private static void SendGmHelpText(Account account) =>
            _agentServerListener.SendSystemMessage(account,
                "Available Commands:<br />" + string.Join("<br />", _gmCommandsDict.Keys));

        private static void ApplySpellTest(NetworkClient client, Account account, string arguments)
        {
            uint id;
            if (arguments.TryParseNormalOrHex(out id))
                ApplySpell(client, account.ClientInstance, account.ClientInstance, id);
        }

        private void HandleGMCommand(NetworkClient client, Account account, string commandText)
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
                _gmCommandsDict[command].Invoke(client, account, arguments);
            else
                _agentServerListener.SendSystemMessage(account, "Unknown command: '" +
                                                                command + "' type .help for info");
        }

        private delegate void GmCommandDelegate(NetworkClient client, Account account, string arguments);
    }
}