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
				
					uint8 spawn1[] = {
						0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xd0, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x0b, 0x69, 0x64, 0x6c, 0x65, 0x32, 0x5f, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
					};
					
					PacketBuffer aBuffer(500);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
					aBuffer.write<uint32>(0x00000047);	//size
					aBuffer.write<uint32>(0xf508f4c1);	//opcode
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(GTable->NPCS[i].npcId);
					aBuffer.writeArray(spawn1, sizeof(spawn1));
					Log.Notice("npc stats2 Pack:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
					aBuffer.doItAll(client->clientSocket);
					
					
					//if(removeNPC(GTable->client, GTable->NPCS[i].npcId, 0x00038c65))
						//spawnNPC(GTable->NPCS[i], GTable);

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
			0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x03, 0xf1, 
			0x3e, 0x4f, 0x4f, 0x3c
		};

		uint8 enemy0_0[] = {
			0x10, 0x00, 
			0x00, 0x01, 0x02, 0x18, 
			0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 
			0x00, 0x00, 0x30, 0xc8, 
			0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0xa4, 0x28, 
			0x00, 0x00, 0x00, 0x00, 
			0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00
		};
	
		uint8 enemy0_1[] = {
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x01, 
			0x02
		};
	
		uint8 enemy0_2[] = {
			0x00, 0x00, 0x00, 0x00, 0x00, 0x04,
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
			0x00, 0x00, 0x00, 0x00
		};

		uint8 enemy0_5[] = {
			0x00, 0x00, 0x03, 0xf1, 
		
			0x00, 0x00, 0x00, 0x03, 
			0x00, 0x00, 0x00, 0x78, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x03, 0xf1
		};

		PacketBuffer aBuffer(10000);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, GTable->client->nClientInst, 0, 0x00); // PassBlob
		uint32 size = sizeof(enemy0_0) + sizeof(enemy0_1) + sizeof(enemy0_3) + sizeof(enemy0_4) + sizeof(enemy0_5) + sizeof(enemy0_2);
		size += (5 * 8) + (27 * 4) + (8 * 2)+ (2 * 1) + sizeof(ending) + 2 + tmpNPC.name.size() + (19 * 4 * npcItems.size());
		
		if(tmpNPC.hairMesh > 0)
		{
			size += (3 * 4) + (1 * 2);
		}
		aBuffer.write<uint32>(size);
		aBuffer.write<uint32>(0xfa015807);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(tmpNPC.npcId);
		aBuffer.write<uint16>(0x0002);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint16>(0x0041);
		aBuffer.write<uint32>(0x00038c65); //instance
		aBuffer.write<uint32>(tmpNPC.position.x);
		aBuffer.write<uint32>(tmpNPC.position.y);
		aBuffer.write<uint32>(tmpNPC.position.z);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(tmpNPC.rotation.x);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(tmpNPC.rotation.y);
		aBuffer.write<uint32>(tmpNPC.rotation.z);
		aBuffer.write<uint32>(0x080ac283);
		aBuffer.write<uint16>(tmpNPC.gender); //gender
		aBuffer.write<uint16>(tmpNPC.race); //race
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
		aBuffer.write<uint32>((tmpNPC.health * 100));
		aBuffer.write<uint32>((tmpNPC.health * 100));
		aBuffer.write<uint16>(0x0032);
		aBuffer.write<uint32>(tmpNPC.bodyMesh);
		aBuffer.write<uint16>(0);
		aBuffer.write<uint32>(tmpNPC.size);
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
			aBuffer.write<uint32>(0);
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
			aBuffer.write<uint32>(0xb290805c);
			aBuffer.write<uint32>(0x29e627ca);
			aBuffer.write<uint32>(0xb290805c);
			aBuffer.write<uint32>(0x29e627ca);
			aBuffer.write<uint32>(0x000003f1);
		}

		aBuffer.writeArray(enemy0_4, sizeof(enemy0_4));
		if(tmpNPC.hairMesh > 0)
		{
			aBuffer.write<uint32>(1);
			aBuffer.write<uint32>(tmpNPC.hairMesh);
			aBuffer.write<uint32>(0x3f800000);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint16>(0);
		}
		else
		{
			aBuffer.write<uint32>(0);
		}
		aBuffer.writeArray(enemy0_5, sizeof(enemy0_5));
		aBuffer.writeArray(ending, sizeof(ending));
		Log.Notice("Spawn Pack:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
		aBuffer.doItAll(GTable->client->clientSocket);
		Log.Debug("Spawned NPC with id: 0x%08x\n", tmpNPC.npcId);
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