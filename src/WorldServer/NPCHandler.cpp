/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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
	Log.Debug("MapId: %i\n", GTable->client->charInfo.map);
	if(GTable->NPCS.size() > 0)
	{
		for(uint32 i = 0; i < GTable->NPCS.size(); i++)
		{
			Log.Debug("NPC %s\n", GTable->NPCS[i].name.c_str());
			if(//GTable->client->charInfo.position.distance(GTable->NPCS[i].position) < maxDistance &&
				//!checkAlreadySpawned(GTable->client->spawnedMobs, GTable->NPCS[i].npcId) &&
				GTable->NPCS[i].mapId == GTable->client->charInfo.map)
			{
				Log.Debug("spawn now\n");
				spawnNPC(GTable->NPCS[i], GTable);
			}
		}
	}
}

bool WorldServer::spawnNPC(NPC tmpNPC, GlobalTable* GTable)
{
	uint8 sender1[] = { 0x0d, 0x13, 0xce, 0x71, 0xb1, 0x10, 0x0b };
	uint8 receiver1[] = { 0x0d, 0x47, 0xc1, 0x67, 0x6c, 0x10, 0xe6, 0x8f, 0x80, 0x08 };
	uint8 npcData1[] = {
		0x18, 0x8c, 0xf4, 0xa0, 0x01, 
					
		0x05, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x01
	};
	uint8 npcData2[] = {
		0x00, 0x00, 0x00, 0x00, 0x78, 0x00, 0x00, 0x00, 
		0x00, 0x41, 0x70, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 
		0x00, 0x00, 0x03, 0xf1, 
		0x41, 0x00, 0x00, 0x82, 0x03, 
		0x00, 0x00, 0x01, 0xf1
	};
	uint8 npcData3[] = {
		0x60, 0x00, 
		0x70, 0x64, 
		0x78, 0x00, 
		0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 
	};
	uint8 npcData4[] = {
		0x90, 0x01, 0x01, 
		0x98, 0x01, 0x01, 
		0xa0, 0x01, 0x64, 
		0xa8, 0x01, 0xc8, 0x61, 
		0xb0, 0x01, 0x01, 
		0xe8, 0x01, 0x03, 
		0x90, 0x02, 0x01, 
		0xa0, 0x02, 0x03, 0x2a, 0x15, 0x08, 0x0f, 0x10, 0x0a, 0x18, 0x05, 0x25, 0x69, 0x2f, 0x00, 0x00, 0x28, 0x01, 0x30, 0x3c, 0x48, 0x80, 0x80, 0x70, 0x70, 0x04, 
		0x3a, 
		0x0d, 0x08, 0xa8, 0xc8, 0x02, 
		0x15, 0x94, 0x7c, 0xff, 0x3e, 
		0x18, 0x01, 
		0x50, 0x00, 0xa0, 0x01, 0x01, 0xa8, 0x01, 0x02, 
		0xba, 0x01, 0x57, 0x08, 0x02, 0x12, 0x53, 0x0a, 0x14, 0x0d, 0xac, 0xfd, 0x24, 0x37, 0x15, 0x2f, 0xc7, 0x77, 0x28, 0x1d, 0xd9, 0x3e, 0xb4, 0xed, 0x25, 0x45, 0x57, 0x26, 0xc2, 0x12, 0x14, 0x0d, 0xac, 0xfd, 0x24, 0x37, 0x15, 0x2f, 0xc7, 0x77, 0x28, 0x1d, 0xd9, 0x3e, 0xb4, 0xed, 0x25, 0x45, 0x57, 0x26, 0xc2, 0x1a, 0x14, 0x0d, 0xac, 0xfd, 0x24, 0x37, 0x15, 0x2f, 0xc7, 0x77, 0x28, 0x1d, 0xd9, 0x3e, 0xb4, 0xed, 0x25, 0x45, 0x57, 0x26, 0xc2, 0x25, 0x6b, 0x42, 0x04, 0x00, 0x2d, 0x6b, 0x42, 0x04, 0x00, 0x38, 0x01, 0x45, 0x43, 0x41, 0x39, 0x4e, 
		0xba, 0x01, 0x57, 0x08, 0x05, 0x12, 0x53, 0x0a, 0x14, 0x0d, 0x17, 0x6a, 0x69, 0x8f, 0x15, 0x54, 0x99, 0xf8, 0x66, 0x1d, 0xf8, 0x7f, 0xc4, 0x78, 0x25, 0xa5, 0x00, 0xfc, 0xf6, 0x12, 0x14, 0x0d, 0x17, 0x6a, 0x69, 0x8f, 0x15, 0x54, 0x99, 0xf8, 0x66, 0x1d, 0xf8, 0x7f, 0xc4, 0x78, 0x25, 0xa5, 0x00, 0xfc, 0xf6, 0x1a, 0x14, 0x0d, 0x17, 0x6a, 0x69, 0x8f, 0x15, 0x54, 0x99, 0xf8, 0x66, 0x1d, 0xf8, 0x7f, 0xc4, 0x78, 0x25, 0xa5, 0x00, 0xfc, 0xf6, 0x25, 0x8d, 0xf4, 0x02, 0x00, 0x2d, 0x8d, 0xf4, 0x02, 0x00, 0x38, 0x01, 0x45, 0x32, 0x4e, 0x37, 0x4f, 
		0xba, 0x01, 0x57, 0x08, 0x06, 0x12, 0x53, 0x0a, 0x14, 0x0d, 0x27, 0xcf, 0xba, 0x0e, 0x15, 0x4f, 0x81, 0xb3, 0x71, 0x1d, 0x5d, 0xb5, 0xe5, 0x22, 0x25, 0x14, 0xb5, 0xd1, 0xab, 0x12, 0x14, 0x0d, 0x27, 0xcf, 0xba, 0x0e, 0x15, 0x4f, 0x81, 0xb3, 0x71, 0x1d, 0x5d, 0xb5, 0xe5, 0x22, 0x25, 0x14, 0xb5, 0xd1, 0xab, 0x1a, 0x14, 0x0d, 0x27, 0xcf, 0xba, 0x0e, 0x15, 0x4f, 0x81, 0xb3, 0x71, 0x1d, 0x5d, 0xb5, 0xe5, 0x22, 0x25, 0x14, 0xb5, 0xd1, 0xab, 0x25, 0x19, 0x4b, 0x02, 0x00, 0x2d, 0x19, 0x4b, 0x02, 0x00, 0x38, 0x05, 0x45, 0x42, 0x32, 0x58, 0x49, 
		0xba, 0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0a, 0x14, 0x0d, 0x9c, 0x57, 0x76, 0xc6, 0x15, 0xd9, 0x1b, 0xd4, 0x80, 0x1d, 0x06, 0x15, 0xe4, 0x41, 0x25, 0xc8, 0x9b, 0x85, 0x95, 0x12, 0x14, 0x0d, 0x9c, 0x57, 0x76, 0xc6, 0x15, 0xd9, 0x1b, 0xd4, 0x80, 0x1d, 0x06, 0x15, 0xe4, 0x41, 0x25, 0xc8, 0x9b, 0x85, 0x95, 0x1a, 0x14, 0x0d, 0x9c, 0x57, 0x76, 0xc6, 0x15, 0xd9, 0x1b, 0xd4, 0x80, 0x1d, 0x06, 0x15, 0xe4, 0x41, 0x25, 0xc8, 0x9b, 0x85, 0x95, 0x25, 0x9f, 0xf4, 0x02, 0x00, 0x2d, 0x9f, 0xf4, 0x02, 0x00, 0x38, 0x01, 0x45, 0x49, 0x33, 0x38, 0x52, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x08, 0x3f, 0x1b, 0xc7, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0a, 0x3f, 0x0a, 0x26, 0xcc, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0c, 0x3f, 0x08, 0x2b, 0x95, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0d, 0x3e, 0xb7, 0x03, 0x4b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0e, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x13, 0x3e, 0xde, 0xd3, 0xc4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x16, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1b, 0x3e, 0xa3, 0x4b, 0x97, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1d, 0x3f, 0x2f, 0xc1, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x3e, 0xa2, 0x07, 0x7c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x3e, 0x64, 0xec, 0x98, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x3e, 0x0f, 0xcb, 0xd5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0x3d, 0x82, 0xb9, 0x4e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x06, 0x3e, 0x21, 0x39, 0xc9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xf1
	};
	/*
	uint8 npcData5[] = {

	};
	uint8 npcData6[] = {

	};
	//*/
		uint32 packSize = sizeof(npcData1) + sizeof(npcData2) + sizeof(npcData3) + sizeof(npcData4) + (29 * 1) + (9 * 2) + (12 * 4) + 1 + tmpNPC.name.size();
		PacketBuffer aBuffer(10000);
		aBuffer.writeHeader(sender1, sizeof(sender1), receiver1, sizeof(receiver1), 0x2000, true);
		aBuffer.write<uint32>(packSize);
		aBuffer.write<uint32>(0xfa015807);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(tmpNPC.npcId);
		aBuffer.write<uint8>(0);
		
		aBuffer.write<uint32>(0x0000002c);
		
		aBuffer.write<uint8>(0x0a); //Id
			aBuffer.write<uint8>(0x0f); //Length
			aBuffer.write<uint8>(0x0d); //axisId
				aBuffer.write<uint32>(tmpNPC.position.x); //axisId
			aBuffer.write<uint8>(0x15); //axisId
				aBuffer.write<uint32>(tmpNPC.position.y); //axisId
			aBuffer.write<uint8>(0x1d); //axisId
				aBuffer.write<uint32>(tmpNPC.position.z); //axisId

		aBuffer.write<uint8>(0x12); //Id
			aBuffer.write<uint8>(0x14); //Length
			aBuffer.write<uint8>(0x0d); //axisId
				aBuffer.write<uint32>(tmpNPC.rotation.a); //axisId
			aBuffer.write<uint8>(0x15); //axisId
				aBuffer.write<uint32>(tmpNPC.rotation.b); //axisId
			aBuffer.write<uint8>(0x1d); //axisId
				aBuffer.write<uint32>(tmpNPC.rotation.c); //axisId
			aBuffer.write<uint8>(0x25); //axisId
				aBuffer.write<uint32>(tmpNPC.rotation.d); //axisId

		aBuffer.writeArray(npcData1, sizeof(npcData1));
		aBuffer.write<uint8>(tmpNPC.typ); //Class
		aBuffer.writeArray(npcData2, sizeof(npcData2));
		aBuffer.write<uint8>(0x0a); //Id
		aBuffer.writeString(tmpNPC.name); //NPC-Name
		aBuffer.write<uint16>(0x2249); 
		aBuffer.write<uint8>(0x08); //Id
		aBuffer.write<uint8>(tmpNPC.gender); //Gender
		aBuffer.write<uint16>(0x1000); 
		aBuffer.write<uint8>(0x18); //Id
		aBuffer.write<uint8>(0x01); //skin color
		aBuffer.write<uint8>(0x20); //Id
		aBuffer.write<uint8>(tmpNPC.size); //Id
		aBuffer.write<uint8>(0x28); //Id
		aBuffer.write<uint16>(0xa7c8); 
		aBuffer.write<uint8>(0x08);
		aBuffer.write<uint8>(0x30);
		aBuffer.write<uint16>(tmpNPC.health);	//Health
		aBuffer.write<uint8>(0x38); //Id
		aBuffer.write<uint16>(tmpNPC.health);	//Health
		aBuffer.write<uint8>(0x40); //Id
		aBuffer.write<uint16>(tmpNPC.stamina);	//Stamina
		aBuffer.write<uint8>(0x48); //Id
		aBuffer.write<uint16>(tmpNPC.stamina);	//Stamina
		aBuffer.write<uint8>(0x50); //Id
		aBuffer.write<uint16>(tmpNPC.mana);	//Mana
		aBuffer.write<uint8>(0x58); //Id
		aBuffer.write<uint16>(tmpNPC.mana);	//Mana
		aBuffer.writeArray(npcData3, sizeof(npcData3));
		aBuffer.write<uint8>(0x88); //Id
		aBuffer.write<uint32>(tmpNPC.headMesh);	//HeadMesh
		aBuffer.writeArray(npcData4, sizeof(npcData4));
		Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
		aBuffer.doItAll(GTable->client->clientSocket);
	return true;
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