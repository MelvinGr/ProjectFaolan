using System;
using System.Collections.Generic;
using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Math;
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
                {"learnspell", (c, a, arg) => LearnSpellTest(c, a, arg)},
                {"teleport", (c, a, arg) => DoTeleport(c, a, arg)},
                {"savedata", (c, a, arg) => SaveData()},
                {"_mount", (c, a, arg) => SendSitOnMountTest(c, a)},
                {"_buff", (c, a, arg) => SendPlayerBuffsTest(c, a)},
                {"_spawn", (c, a, arg) => SendSpawnNPCAndPlayersTest(c)},
                {"_casteffect", (c, a, arg) => ApplySpellTest(c, a, arg)},
                {"_kalanthes", (c, a, arg) => SpawnKalanthesTest(c)},
                {"_spawnobjtest", (c, a, arg) => SpawnObjectTest(c, a)},
                {"settimeofday", (c, a, arg) => SetTimeofDay(c)},
                {"printpos", (c, a, arg) => _agentServerListener.SendSystemMessage(a, a.Character.Position.ToString())},
            };
        }

        private void SaveData()
        {
            SaveAllCharacterData();
        }

        private static void DoTeleport(NetworkClient client, Account account, string arguments)
        {
            UInt32 mapId;
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

            var sender = new byte[] {0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b};
            var receiver = new byte[] {0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08};

            account.Character.Map = map.Id;
            account.Character.Position.X = map.Position.X;
            account.Character.Position.Y = map.Position.Y;
            account.Character.Position.Z = map.Position.Z;
            account.Character.Rotation.X = map.Rotation.X;
            account.Character.Rotation.Y = map.Rotation.Y;
            account.Character.Rotation.Z = map.Rotation.Z;

            //Save new Position
            //Database.saveCharPosition(client);

            //Log.Notice("Teleporting\nMapId: %i\nPOS X: %i\nPOS Y: %i\nPOS Z: %i\n\n", map.mapID, map.Position.X, map.Position.Y, map.Position.Z);

            new PacketStream()
                .WriteHeader(sender, receiver, null, 0x2000)
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
                .WriteHeader(sender, receiver, null, 0x2000)
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

            Send0x5D85BFC7(client);
            SendReadyForPlayScreen(client, account);
            SpawnPlayer(client, account);
        }

        private static void SendListAllMaps(Account account) =>
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", ConanMap.AllMaps.Select(m => m.Id + " : " + m.Name)));

        private static void SendListAllSpells(Account account) =>
            _agentServerListener.SendSystemMessage(account,
                string.Join("<br />", Spell.AllSpells.Select(m => m.Id + " : " + m.Name)));

        private static void SendGmHelpText(Account account) =>
            _agentServerListener.SendSystemMessage(account, 
                "Available Commands:<br />" + string.Join("<br />", _gmCommandsDict.Keys));

        private static void LearnSpellTest(NetworkClient client, Account account, string arguments)
        {
            UInt32 id;
            if (arguments.TryParseNormalOrHex(out id))
            {
                new PacketStream()
                    .WriteHeader(Sender3, Receiver3, null, 0x09)
                    .WriteUInt32(id)
                    .Send(client);
            }
        }

        private static void SpawnObjectTest(NetworkClient client, Account account)
        {
            var sender = new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x4C };
            var receiver = new byte[] { 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xD4, 0xCB, 0x8B, 0x40 };

            var packetData2 = new byte[]
            {
                0x00, 0x00, 0xC8, 0x78,
                0x00, 0x00, 0x87, 0x10,
                0x00, 0x00, 0x00, 0x00,

                0x2B,
                0x0A, 0x0F, 0x0D, 0x66,
                0x12, 0x7A, 0x44, 0x15,
                0x6B, 0x93, 0x31, 0x43,
                0x1D, 0x0E, 0x15, 0x93,
                0x44, 0x12, 0x14, 0x0D,

                0x00, 0x00, 0x00, 0x00,
                0x15, 0x7F, 0x2E, 0x3E,
                0xBF, 0x1D,
                0x00, 0x00, 0x00, 0x00,
                0x25, 0x31, 0x5E, 0x2B,
                0x3F, 0x18, 0xBA, 0xEC,
                0x43,
                0x00, 0x00, 0x00, 0x72,
                0x08, 0x00,

                0x12,
                0x11,// the neelde of set
                0x54, 0x68, 0x65, 0x20, 0x4E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x20, 0x6F, 0x66, 0x20,
                0x53, 0x65, 0x74,

                0x1A,
                0x57,
                0x3C, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x69, 0x7A, 0x65, 0x64, 0x20, 0x69, 0x64, 0x3D,
                0x22, 0x33, 0x35, 0x39, 0x35, 0x36, 0x31, 0x37, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65,
                0x67, 0x6F, 0x72, 0x79, 0x3D, 0x22, 0x35, 0x35, 0x30, 0x30, 0x30, 0x22, 0x20, 0x66,
                0x6C, 0x61, 0x67, 0x73, 0x3D, 0x22, 0x22, 0x20, 0x6B, 0x65, 0x79, 0x3D, 0x22, 0x6F,
                0x59, 0x29, 0x47, 0x61, 0x6B, 0x44, 0x62, 0x26, 0x61, 0x6D, 0x70, 0x3B, 0x67, 0x26,
                0x71, 0x75, 0x6F, 0x74, 0x3B, 0x4F, 0x4D, 0x41, 0x53, 0x38, 0x38, 0x4D, 0x60, 0x29,
                0x22, 0x20, 0x3E,

                0x28, 0x01, 0x60, 0x01,

                0x00, 0x35, 0x05, 0x2C,
                0x00, 0x0F, 0x69, 0x6E,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,

                0xB9, 0xAE, 0x1A, 0x66,
                0x3B, 0x08, 0x0E, 0x66,
                0xBC, 0x58, 0x98, 0x46,

                0x3E, 0xFC, 0xAC, 0xC6,
                0x3E, 0x10, 0x54, 0x7F,
                0x3C, 0x1F, 0xE0, 0xDB,

                0x3F,
                0x80, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00
            };

            new PacketStream() 
                .WriteHeader(sender, receiver, null, 0x00002000)
                .WriteArrayPrependLengthUInt32(new ConanStream()
                    .WriteUInt32(Ox2000RespondsOpcodes.SpawnObject)
                    .WriteArray(packetData2))
                .Send(client);
        }

        private static void ApplySpellTest(NetworkClient client, Account account, string arguments)
        {
            UInt32 id;
            if (arguments.TryParseNormalOrHex(out id))
                ApplySpell(client, account.ClientInstance, account.ClientInstance, id);
        }

        private void HandleGMCommand(NetworkClient client, Account account, string commandText)
        {
            // Should do a gm check here, send error of not allowed blabla

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