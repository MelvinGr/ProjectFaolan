/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#include "WorldServer.h"

void WorldServer::GameCharAgentHandler(Packet* packet, GlobalTable* GTable)
{
	GameClient* client = GTable->client;
	Log.Warning("Receive Packet With Opcode: 0x%08X (%s):\n", packet->opcode, packet->receiver.c_str());
	switch(packet->opcode)
	{
	case 0x01: // RequestAggroStatus
		{
			Log.Debug("GCA_RequestAggroStatus\n");
			uint32 unk1 = packet->data->read<uint32>(); // 00 00 C3 50 
			uint32 clientInst = packet->data->read<uint32>(); // 11 01 89 CA 
			uint32 unk3 = packet->data->read<uint32>(); // 00 00 C3 50 
			uint32 unk4 = packet->data->read<uint32>(); // 00 06 A1 27

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x01); // UpdateAggroStatus
			aBuffer.write<uint32>(0x0000C350); // = 50000 - WTF is this? (It is all over the place) 
			aBuffer.write<uint32>(0x0006A12A); // npc ID?
			aBuffer.write<uint32>(0x00000002); // aggro value?
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x4F:
		{			
			uint32 attID = packet->data->read<uint32>(); // i think this is the spellID... 
			// 0x00000103 = switch weapon
			// 0x000004e2 = toggle combat
			// 0x000004e4 = crouch

			switch(attID)
			{
			default:
				{
					NPC curCombat = getNpcData(GTable, client->charInfo.combat.target);

					if(curCombat.fraction > 0)
					{
						//client->charInfo.Combat.sendAt4B=false;
						//Log.Notice("ATT TARGET 0x%08x\n", client->charInfo.combat.target);

						PacketBuffer aBuffer(500);
						if(client->nClientInst != curCombat.combat.target)
						{
							curCombat.combat.target = client->nClientInst;

							aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
							aBuffer.write<uint32>(0x0000001d);
							aBuffer.write<uint32>(0xe501139d); //sets symbol over head (in combat)
							aBuffer.write<uint32>(0x0000c350);
							aBuffer.write<uint32>(client->nClientInst);
							aBuffer.write<uint8>(0x00);
							aBuffer.write<uint32>(0x0000c350);
							aBuffer.write<uint32>(client->charInfo.combat.target);
							aBuffer.write<uint32>(0x00000001);
							aBuffer.write<uint32>(0x3e4f4f3c);
							aBuffer.doItAll(client->clientSocket);

							//updateNpcData(GTable, curCombat);
						}
					}
					break;
				}
			}

			break;
		}

	case 0x56:
		{
			Log.Debug("GET OPCODE 0x56 !!!!!\n");
			uint32 data = packet->data->read<uint32>();
			uint32 clientInst = packet->data->read<uint32>();
			uint8 data1 = packet->data->read<uint8>();

			break;
		}

	case Opcodes::GCA_SpellbarRequest:	//Spellbar request
		{
			Log.Debug("GCA_SpellbarRequest\n");
			vector<Item> charBar;
			Database.getCharBar(client->charInfo.characterID, &charBar);
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GCI_SpellBarData);
			aBuffer.write(((charBar.size() + 1) * 1009));
			for(uint32 c = 0; c < charBar.size(); c++)
			{
				aBuffer.write<uint32>(charBar[c].pos);
				aBuffer.write<uint32>(4);
				aBuffer.write<uint32>(charBar[c].id);
				aBuffer.write<uint32>(0);
				aBuffer.write<uint32>(0);
				aBuffer.write<uint32>(0);
			}
			aBuffer.doItAll(client->clientSocket);
			Log.Debug("GCI_SpellbarData\n");
			break;
		}

	case Opcodes::GCA_Ping: //Ping
		{
			Log.Debug("GCA_Ping\n");
			sendPackets::GCI_PingPong(packet, client);
			break;
		}

	case 0x7e:
		{
			uint32 data = packet->data->read<uint32>(); // This is some sort of counter

			time_t nServerTime;
			time(&nServerTime);

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x76);
			aBuffer.write<uint32>(data);
			aBuffer.write<uint32>(0x0000001c);	// ???
			aBuffer.write<uint32>(nServerTime);
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x1E: //UpdateQuestMarkerNPC
		{
			uint32 data = packet->data->read<uint32>(); // 00 00 c3 50
			uint32 data1 = packet->data->read<uint32>(); 
			uint32 data2 = packet->data->read<uint32>(); // 00 00 c3 50
			uint32 clientInst = packet->data->read<uint32>();

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x08);
			aBuffer.write<uint32>(data);
			aBuffer.write<uint32>(data1);
			/*
			00 = nothing
			02 = quest avaliable (! over head)
			04 = quest complete (? over head)
			*/
			
			aBuffer.write<uint8>(0x00);
			
			aBuffer.doItAll(client->clientSocket);
			
			break;
		}

	case 0x55:
		{
			/*
			Data of client packet:
			00 00 c3 50 00 06 9c e0 
			00 0a //stringlength
			7b 73 74 6f 63 6b 5f 34 38 7d //{stock_48}
			00 2d ad 4d
			*/
			uint32 data = packet->data->read<uint32>();
			uint32 obj = packet->data->read<uint32>();	//i think object id
			string command = packet->data->read<string>();
			uint32 data2 = packet->data->read<uint32>();
			if(command == "{stock_48}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6195\" category=\"50000\" key=\"gdVvZ+XDaiyqOA_OQ+_.\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);

				aBuffer = PacketBuffer(500);
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
				aBuffer.write<uint32>(0x0000001b);
				aBuffer.write<uint32>(0xa36d3b74);
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint32>(0x00040202);
				aBuffer.write<string>("Kneel");
				aBuffer.write<uint32>(0x3e4f4f3c);
				aBuffer.doItAll(client->clientSocket);

				if(!Database.insertItemEntry(client->charInfo.characterID))
				{
					Log.Error("Can't insert entry at player_items\n");
				}
			}
			if(command == "{stock_49}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6196\" category=\"50000\" key=\"Uj1v&amp;fwEoqN_lA8bnWOp\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);
			}
			if(command == "{stock_50}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6197\" category=\"50000\" key=\"O,AyOT)I7kT+ECrK(V.?\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);
			}
			if(command == "{stock_51}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6198\" category=\"50000\" key=\"K&#39;B*LKSZ*)%&#92;mr^^wf0^\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);
			}
			if(command == "{stock_52}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6199\" category=\"50000\" key=\"!-u62.+NE`(`enfqa-MH\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);
			}
			if(command == "{stock_56}")
			{
				PacketBuffer aBuffer(2000);
				aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x57);
				aBuffer.write<uint32>(data2);
				aBuffer.write<string>("<remoteformat id=\"6200\" category=\"50000\" key=\"l0YEA(3XEbloy4HkuHFC\" knubot=\"388\"  ></remoteformat>");
				aBuffer.doItAll(client->clientSocket);
			}
			break;
		}

	case 0x75: // SetHideHelmet
		{
			uint8 unk1 = packet->data->read<uint8>(); // 00
			if(unk1)
				Log.Notice("SetHideHelmet ON\n");
			else
				Log.Notice("SetHideHelmet OFF\n");

			break;
		}

	case 0x76: // SetHideCloak
		{
			uint8 unk1 = packet->data->read<uint8>(); // 00
			if(unk1)
				Log.Notice("SetHideCloak ON\n");
			else
				Log.Notice("SetHideCloak OFF\n");

			break;
		}

	case 0x77: // SetHideInspectOption
		{
			uint8 unk1 = packet->data->read<uint8>(); // 00
			if(unk1)
				Log.Notice("SetHideInspectOption ON\n");
			else
				Log.Notice("SetHideInspectOption OFF\n");

			break;
		}

		case 0x21: //ValidResurrectionSpots
		{
			uint32 SpotID = packet->data->read<uint32>();

			 PacketBuffer aBuffer(500);
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
				aBuffer.write<uint32>(0x00000052);
				aBuffer.write<uint32>(0x642cd3d6);
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint16>(0x0004);
				aBuffer.write<uint32>(0x00000025);
				aBuffer.write<uint32>(0x0000a198);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000002);
				aBuffer.write<uint32>(0x00000008);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000064);
				aBuffer.write<uint32>(0x00000003);
				aBuffer.write<uint32>(SpotID);
				aBuffer.write<uint32>(0x3e4f4f3c);
				aBuffer.doItAll(client->clientSocket);


				//Here there must be ressurection and bind system 
				//Example Ressurect packet

				uint8 pack1_0[] = {0x00, 0x44, 0x14, 0x4d, 0x71, 0x43, 0x31, 0x4b, 0x14, 0x44, 0x33, 0xb0, 0xa4, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x67, 0x17, 0x3c, 0x00, 0x00, 0x00, 0x00, 0x3e, 0xdc, 0x4c, 0xe5, 0x62, 0x00, 0x00, 0xc7, 0xa0, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x00, 0x00, 0x01, 0x09, 0xe2, 0x7d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x86, 0xaa, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x3e, 0x4f, 0x4f, 0x3c};
				aBuffer = PacketBuffer(500);
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
				aBuffer.write<uint32>(0x0000005f);
				aBuffer.write<uint32>(0x5a32f0d7);
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.writeArray(pack1_0,sizeof(pack1_0));
				aBuffer.doItAll(client->clientSocket);
				client->charInfo.hp+=100;

			Log.Debug("Received spot ID: %u",SpotID);

		}break;

	default:
		{
			Log.Warning("Unknown Packet With Opcode: 0x%08X (%s):\n", packet->opcode, packet->receiver.c_str());
			Log.Warning("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());

			break;
		}
	}
}