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

void WorldServer::LoadNearNPCs(GlobalTable* GTable)
{
	GameClient* client = GTable->client;

	if(GTable->NPCS.size() > 0)
	{
		for(uint32 i = 0; i < GTable->NPCS.size(); i++)
		{
			if(GTable->client->charInfo.position.distance(GTable->NPCS[i].position) < maxDistance &&
				!checkAlreadySpawned(GTable->client->spawnedMobs, GTable->NPCS[i].npcId) &&
				GTable->NPCS[i].mapId == GTable->client->charInfo.map)
			{
				spawnNPC(GTable->NPCS[i], GTable);
				
				uint32 data[7];
				data[0] = 3;
				data[1] = MODHEALTH;
				data[2] = GTable->NPCS[i].curHealth;
				data[3] = MODSTAMINA;
				data[4] = GTable->NPCS[i].curStamina;
				data[5] = MODMANA;
				data[6] = GTable->NPCS[i].curMana;
				sendPackets::passBlob::CharStats(GTable->client, GTable->NPCS[i].npcId, data);
				//*/
				
				if(GTable->NPCS[i].fraction > 0)
				{
					uint8 spawn1[] = {
						0x00, 0x04, 
						0x00, 0x00, 0x00, 0x07, 
						0x00, 0x00, 0x42, 0x58, 
						0x00, 0x00, 0x00, 0x00, 
						0x00, 0x00, 0x00, 0x01, 
						0x00, 0x00, 0x00, 0x18, 
						0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
					};
					PacketBuffer aBuffer(500);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
					aBuffer.write<uint32>(0x0000004a);	//size
					aBuffer.write<uint32>(0x642cd3d6);	//opcode
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(GTable->NPCS[i].npcId);
					aBuffer.writeArray(spawn1, sizeof(spawn1));
					aBuffer.doItAll(client->clientSocket);
					
					
					if(removeNPC(GTable->client, GTable->NPCS[i].npcId, 0x00038c65))
						spawnNPC(GTable->NPCS[i], GTable);
				}
				/*
				if(GTable->NPCS[i].extras.size() > 0 && false)
				{
					vector<string> extras = String::splitString(GTable->NPCS[i].extras, "::");
					uint32 anzPackets = atoi(extras[0].c_str());
					if(anzPackets > 0)
					{
						for(uint32 x = 0; x < anzPackets; x++)
						{
							vector<string> packet = String::splitString(extras[x+1], "<->");
							PacketBuffer aBuffer(5000);
							aBuffer.writeHeader(packet[0], packet[1], gameUnknown1, 0, GTable->client->nClientInst, 0, atoi(packet[2].c_str()));
							if(atoi(packet[3].c_str())>0)
							{
								uint8 toWrite;
								uint32 offset = 0;
								aBuffer.write<uint32>(atoi(packet[3].c_str()));
								for(int y = 0; y < atoi(packet[3].c_str()); y++)
								{
									uint8 conv[2];
									conv[0] = GetDigitValue(packet[4].c_str()[offset]);
									conv[1] = GetDigitValue(packet[4].c_str()[offset+1]);
									toWrite = (conv[0] * 16) + conv[1];
									offset += 2;
									toWrite &= 0x000000ff;
									aBuffer.write<uint8>(toWrite);
								}
								Log.Notice("\n");

							}
							aBuffer.doItAll(client->clientSocket);
						}
					}
				}
				//*/
			}
		}
	}
}

bool WorldServer::spawnNPC(NPC tmpNPC, GlobalTable* GTable)
{
	try
	{
		vector<Item> npcItems;
		Database.getNpcItems(tmpNPC.npcId, &npcItems);
	
		uint8 ending[] = 
		{
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x3e, 0x4f, 0x4f, 0x3c
		};

		uint8 enemy0_0[] = {
			0x10, 0x00, 
			0x00, 0x01, 0x01, 0x16, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 
			0x00, 0x00, 0x30, 0xc8, 
			0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0xa4, 0x28, 
			0x3f, 0x80, 0x00, 0x00, 
			0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00
		};
	
		uint8 enemy0_1[] = {
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x01, 
			0x01
		};
	
		uint8 enemy0_2[] = {
			//0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 
			0x3c, 0x00, 0x0f, 0x0a, 0x05, 0x04,
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00
		};
	
		uint8 enemy0_3[] = {
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x64, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 
			0x00, 0x00, 0x00, 0x05, 
			0x00, 0x00, 0x00, 0x02, 
			0x00
		};
	
		uint8 enemy0_4[] = {
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 

			0x00, 0x00, 0x00, 0x01, //16
			/*
			0x00, 0x00, 0x00, 0x08, 0x3f, 0x1b, 0xc7, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x0a, 0x3f, 0x09, 0x25, 0xe2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x0c, 0x3f, 0x08, 0x2b, 0x95, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x0d, 0x3e, 0xb7, 0x03, 0x4b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x13, 0x3e, 0xdf, 0xd4, 0x5b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x1b, 0x3e, 0xa4, 0xd0, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x1d, 0x3f, 0x2f, 0xd8, 0xfb, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x20, 0x3e, 0xa2, 0x5a, 0xf0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x01, 0x00, 0x3e, 0x5e, 0xf3, 0xe1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x01, 0x01, 0x3e, 0x0f, 0xcb, 0xd5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0xff, 0x3d, 0x82, 0xb9, 0x4e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x01, 0x06, 0x3e, 0x21, 0x39, 0xc9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			//*/
		};

		uint8 enemy0_5[] = {
			0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x03, 0xf1, 
		
			0x00, 0x00, 0x00, 0x04, 
			0x00, 0x00, 0x00, 0x03, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x03, 0xf1
		};

		PacketBuffer aBuffer(10000);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, GTable->client->nClientInst, 0, 0x00); // PassBlob
		uint32 size = sizeof(enemy0_0) + sizeof(enemy0_1) + sizeof(enemy0_3) + sizeof(enemy0_4) + sizeof(enemy0_5) + sizeof(enemy0_2);
		size += (31 * 4) + (5 * 2) + (4 * 1) + sizeof(ending) + 2 + tmpNPC.name.size() + (14 * 4 * npcItems.size());
	
		aBuffer.write<uint32>(size);
		aBuffer.write<uint32>(0xfa015807);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(tmpNPC.npcId);
		aBuffer.write<uint16>(0x0002);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0x0000002a);
		aBuffer.write<uint32>(0x00000000);
		aBuffer.write<uint32>(0x00000000);
		aBuffer.write<uint32>(0x295fe750);
		aBuffer.write<uint8>(0x41);
		aBuffer.write<uint32>(0x00038c65); //instance
		aBuffer.write<uint32>(0x080ac283);
		aBuffer.write<uint32>(tmpNPC.position.x);
		aBuffer.write<uint32>(tmpNPC.position.y);
		aBuffer.write<uint32>(tmpNPC.position.z);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(tmpNPC.rotation.x);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(tmpNPC.rotation.y);
		aBuffer.write<uint32>(tmpNPC.rotation.z);
		aBuffer.write<string>(tmpNPC.name);
		aBuffer.writeArray(enemy0_0, sizeof(enemy0_0));
		aBuffer.write<uint32>((tmpNPC.stamina * 100));
		aBuffer.write<uint32>((tmpNPC.stamina * 100));
		aBuffer.write<uint32>((tmpNPC.mana * 100));
		aBuffer.write<uint32>((tmpNPC.mana * 100));
	
		if(tmpNPC.fraction > 0)
			aBuffer.write<uint8>(0x00);
		else
			aBuffer.write<uint8>(0x01);
				
		aBuffer.write<uint32>(tmpNPC.action);
		aBuffer.writeArray(enemy0_1, sizeof(enemy0_1));
		aBuffer.write<uint32>(tmpNPC.nameId);
		aBuffer.writeArray(enemy0_2, sizeof(enemy0_2));
		aBuffer.write<uint16>(tmpNPC.level);
		aBuffer.write<uint16>(tmpNPC.level);
		aBuffer.write<uint32>((tmpNPC.health));
		aBuffer.write<uint32>((tmpNPC.health));
		aBuffer.write<uint16>(0x0032);
		//aBuffer.writeArray(enemy0_2, sizeof(enemy0_2));
		aBuffer.write<uint32>(tmpNPC.bodyMesh);
		aBuffer.write<uint16>(tmpNPC.size);
		aBuffer.writeArray(enemy0_3, sizeof(enemy0_3));
		aBuffer.write<uint32>(tmpNPC.headMesh);
		aBuffer.write<uint32>(0x000003f1);
		aBuffer.write<uint8>(0x01);

		aBuffer.write<uint32>(npcItems.size());
		for(uint32 c = 0; c < npcItems.size(); c++)
		{
			aBuffer.write<uint32>(npcItems.at(c).pos);
			aBuffer.write<uint32>(npcItems.at(c).id);
			aBuffer.write<uint32>(npcItems.at(c).id);
			aBuffer.write<uint32>(npcItems.at(c).iLevel);
			aBuffer.write<uint32>(npcItems.at(c).data0);
			aBuffer.write<uint32>(npcItems.at(c).data1);
			aBuffer.write<uint32>(npcItems.at(c).data2);
			aBuffer.write<uint32>(npcItems.at(c).data3);
			aBuffer.write<uint32>(npcItems.at(c).data4);
			aBuffer.write<uint32>(npcItems.at(c).data1);
			aBuffer.write<uint32>(npcItems.at(c).data2);
			aBuffer.write<uint32>(npcItems.at(c).data3);
			aBuffer.write<uint32>(npcItems.at(c).data4);
			aBuffer.write<uint32>(0x000003f1);
		}

		aBuffer.writeArray(enemy0_4, sizeof(enemy0_4));
		aBuffer.write<uint32>(tmpNPC.hairMesh);
		aBuffer.writeArray(enemy0_5, sizeof(enemy0_5));
		aBuffer.writeArray(ending, sizeof(ending));
		aBuffer.doItAll(GTable->client->clientSocket);

		return true;
	}
	catch(char* errMsg)
	{
		Log.Error("Error at spawning NPC on the map\nError-Message:\n%s\n", errMsg);
		return false;
	}
}

bool WorldServer::removeNPC(GameClient* client, uint32 npcId, uint32 instance)
{
	try
	{
		PacketBuffer aBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
		aBuffer.write<uint32>(0x00000015);	//size
		aBuffer.write<uint32>(0x04b82b2d);	//opcode
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(npcId);
		aBuffer.write<uint8>(1);
		aBuffer.write<uint32>(instance);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);

		return true;
	}
	catch(char* errMsg)
	{
		Log.Error("Error at removing NPC from the map\nError-Message:\n%s\n", errMsg);
		return false;
	}
}

bool WorldServer::checkAlreadySpawned(vector<uint32>* spawned, uint32 npcId)
{
	if(spawned->size() > 0)
	{
		for(uint32 x = 0; x < spawned->size(); x++)
			if(spawned->at(x) == npcId) 
				return true;
	}

	return false;
}
uint8 WorldServer::GetDigitValue (char digit)
{
	int asciiOffset, digitValue;
	if (digit >= 48 && digit <= 57)
	{
		// code for '0' through '9'
		asciiOffset = 48;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else if (digit >= 65 && digit <= 70)
	{
		// digit is 'A' through 'F'
		asciiOffset = 55;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else if (digit >= 97 && digit <= 102)
	{
		// code for 'a' through 'f'
		asciiOffset = 87;
		digitValue = digit - asciiOffset;
		return digitValue;
	}
	else
	{
		// illegal digit
		return 0;
	}
}

void WorldServer::eraseAllNpcs(GameClient* client)
{
	for(uint32 i = 0; i < client->spawnedMobs->size(); i++)
	{
		PacketBuffer aBuffer(5000);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x00000015);
		aBuffer.write<uint32>(0x04b82b2d);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->spawnedMobs->at(i));
		aBuffer.write<uint8>(0x01);
		aBuffer.write<uint32>(0x00038c65);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);
	}

	client->spawnedMobs->clear();
}