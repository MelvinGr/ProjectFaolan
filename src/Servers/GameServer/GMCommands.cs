using System;
using System.Collections.Generic;
using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;
using LibFaolan.Other;

namespace GameServer
{
    partial class GameServerListener
    {
        private static readonly Dictionary<string, GmCommandDelegate> GmCommandsDict;

        static GameServerListener()
        {
            GmCommandsDict = new Dictionary<string, GmCommandDelegate>
            {
                {"help", (c, a, arg) => SendGmHelpText(a)},
                {"broadcast", (c, a, arg) => _agentServerListener.SystemBroadcast(arg)},
                {"listmaps", (c, a, arg) => SendListAllMaps(a)},
                {"teleport", (c, a, arg) => DoTeleport(c, a, arg)},
                {"_mount", (c, a, arg) => SendSitOnMountTest(c, a)},
                {"_buff", (c, a, arg) => SendPlayerBuffsTest(c, a)},
                {"_spawn", (c, a, arg) => SendSpawnNPCAndPlayersTest(c)},
                {"_casteffect", (c, a, arg) => ApplySpellTest(c, a, arg)}
            };
        }

        private static void DoTeleport(NetworkClient client, Account account, string arguments)
        {
            UInt32 mapId;
            if (!arguments.TryParseNormalOrHex(out mapId))
            {
                _agentServerListener.SendSystemMessage(account, "Invalid input: '" + arguments + "'");
                return;
            }

            var map = Database.AllMaps.FirstOrDefault(m => m.Id == mapId);
            if (map == null)
            {
                _agentServerListener.SendSystemMessage(account, "Unknown mapId: '" + arguments + "'");
                return;
            }

            var sender = new byte[] { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b };
            var receiver = new byte[] { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08 };

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
                string.Join("<br />", Database.AllMaps.Select(m => m.Id + " : " + m.Name)));

        private static void SendGmHelpText(Account account) =>
            _agentServerListener.SendSystemMessage(account, "Available Commands:<br/>"
                + string.Join("<br />", GmCommandsDict.Keys));

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

            if (GmCommandsDict.ContainsKey(command))
                GmCommandsDict[command].Invoke(client, account, arguments);
            else
                _agentServerListener.SendSystemMessage(account, "Unknown command: '" +
                    command + "' type .help for info");
        }

        private delegate void GmCommandDelegate(NetworkClient client, Account account, string arguments);
    }
}