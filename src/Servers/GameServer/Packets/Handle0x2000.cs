using System.Linq;
using LibFaolan.Data;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace GameServer
{
    partial class GameServerListener
    {
        private void Handle0X2000(NetworkClient client, Account account, ConanPacket packet)
        {
            var pbLength = packet.Data.ReadUInt32();
            var pbOpcode = (DataOpcodes) packet.Data.ReadUInt32();
            Logger.WriteLine("Received data opcode: " + pbOpcode + " (" + pbOpcode.ToHex() + ")");

            switch (pbOpcode)
            {
                case DataOpcodes.Oxf98E10B3:
                {
                    Handle0Xf98E10B3(client, account, packet);
                    break;
                }
                case DataOpcodes.Ox36D3B74:
                {
                    HandleCommandPacket(client, account, packet);
                    break;
                }
                case DataOpcodes.Move:
                {
                    var dataobjdec = packet.Data.ReadUInt32();
                    var dataclientinst = packet.Data.ReadUInt32();
                    var movingType = (MovingTypes) packet.Data.ReadUInt16();

                    switch (movingType)
                    {
                        case MovingTypes.Run:
                        case MovingTypes.Walk:
                        case MovingTypes.Jump:
                        case MovingTypes.WalkJump:
                        case MovingTypes.MountedJump:
                        case MovingTypes.Ox011A:
                        case MovingTypes.Ox011E:
                        case MovingTypes.Ox001E:
                        case MovingTypes.Ox001A:
                        {
                            var charCoords = packet.Data.ReadVector3();
                            var datadat = packet.Data.ReadArray(pbLength - 6*4 + 1*2);

                            //var recvClient = Accounts.FirstOrDefault(a => a.ClientInstance == dataclientinst);
                            //recvClient.Character.Position = charCoords;
                            account.Character.Position = charCoords;

                            // send to all (nearby) clients?
                            new PacketStream().WriteHeader(Sender5, Receiver5, null, 0x2000)
                                .WriteArrayPrependLengthUInt32(new ConanStream()
                                    .WriteUInt32(pbOpcode) 
                                    .WriteUInt32(dataobjdec)
                                    .WriteUInt32(dataclientinst)
                                    .WriteUInt16(movingType)
                                    .WriteVector3(charCoords)
                                    .WriteArray(datadat))
                                .Send(client);

                            break;
                        }
                        default:
                        {
                            Logger.WriteLine("Received unknown moving type: " + movingType.ToHex());
                            break;
                        }
                    }
                    break;
                }
                case DataOpcodes.ReadyForPlayScreen: //peer0_160
                {
                    SendReadyForPlayScreen(client, account);
                    if (account.Character.Level == 0)
                        HandleNewCharacter(client, account, packet);

                    break;
                }
                case DataOpcodes.InteractWithObject:
                {
                    var cInstPreId = packet.Data.ReadUInt32();
                    var clientInst = packet.Data.ReadUInt32();
                    var unk0 = packet.Data.ReadUInt16();
                    var unk1 = packet.Data.ReadUInt32();
                    var unkdata = packet.Data.ReadArrayPrependLengthUInt32();
                    var objPreId = packet.Data.ReadUInt32();
                    var objId = packet.Data.ReadUInt32();
                    var unk2 = packet.Data.ReadUInt32();

                    Logger.WriteLine("Interacting with object");

                    break;
                }
                case DataOpcodes.CloseGame:
                {
                    var unk0 = packet.Data.ReadUInt32();
                    var accountId = packet.Data.ReadUInt32();

                    Logger.WriteLine("Received close game! (x button)");
                    break;
                }
                case DataOpcodes.SelectDeselect:
                {
                    var data = packet.Data.ToArray();

                    Logger.WriteLine("SelectDeselect!");

                    /*
                    uint32 length = packet->data->bufferLength - 4;

	                switch(length)
	                {
		                case PassBlob::SELECT_NPC:
			                {
				                Log.Debug("Receive GA_PassBlob - SELECT_OBJECT - SELECT_NPC\n");
				                try
				                {
					                uint32 data = packet->data->read<uint32>();
					                uint32 ClientInst = packet->data->read<uint32>();
					                uint8 unk0 = packet->data->read<uint8>();
					                uint32 data2 = packet->data->read<uint32>();
					                client->charInfo.combat.target = packet->data->read<uint32>();
					                uint32 unk1 = packet->data->read<uint32>();
					                uint32 unk2 = packet->data->read<uint32>();
					                uint32 end = packet->data->read<uint32>();

					                PacketBuffer aBuffer(500);
					                aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, GI_PassBlob);
					                aBuffer.write<uint32>(length);
					                aBuffer.write<uint32>(SELECT_OBJECT);
					                aBuffer.write<uint32>(data);
					                aBuffer.write<uint32>(ClientInst);
					                aBuffer.write<uint8>(unk0);
					                aBuffer.write<uint32>(data2);
					                aBuffer.write<uint32>(client->charInfo.combat.target);
					                aBuffer.write<uint32>(unk1);
					                aBuffer.write<uint32>(unk2);
					                aBuffer.write<uint32>(end);
					                aBuffer.doItAll(client->clientSocket);
					                Log.Debug("Sent GI_PassBlob - SELECT_OBJECT - SELECT_NPC\n");
				                }
				                catch(char* errMsg)
				                {
					                Log.Error("Error at receiving SelectionDatas @ SELECT_NPC - SelectObjet.cpp\nError Message:\n%s\n", errMsg);
				                }
				                break;
			                }

		                default:
			                {
				                Log.Debug("Receive GA_PassBlob - SELECT_OBJECT - Unknown Length: 0x%08x\n", length);
				                try
				                {
					                PacketBuffer aBuffer(500);
					                aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					                aBuffer.write<uint32>(length);
					                aBuffer.write<uint32>(SELECT_OBJECT);
					                aBuffer.writeArray(packet->data->readArray(length-4), length-4);
					                aBuffer.doItAll(client->clientSocket);
					                Log.Debug("Sent GI_PassBlob - SELECT_OBJECT - Unknown\n");
				                }
				                catch(char* errMsg)
				                {
					                Log.Error("Error at receiving SelectionDatas @ SELECT_NPC - SelectObjet.cpp\nError Message:\n%s\n", errMsg);
				                }
				                break;
			                }
	                }                    
                    */


                    break;
                }
                default:
                {
                    var data = packet.Data.ToArray();

                    Logger.WriteLine("Unknown data Opcode: " + pbOpcode.ToHex());
                    break;
                }
            }
        }
    }
}