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

#include "../WorldServer.h"

void sendPackets::passBlob::objHandle::initObject(GlobalTable* GTable)
{
	GameClient* client = GTable->client;

	if(GTable->OBJECTS.size() > 0)
	{
		for(uint32 i = 0; i < GTable->OBJECTS.size(); i++)
		{
			if(GTable->client->charInfo.position.distance(GTable->OBJECTS[i].position) < maxDistance &&
				!checkAlreadySpawned(GTable->client->spawnedObj, GTable->OBJECTS[i].id) &&
				GTable->OBJECTS[i].map == GTable->client->charInfo.map)
			{
				spawnObj(GTable->OBJECTS[i], GTable);
			}
		}
	}
}

bool sendPackets::passBlob::objHandle::spawnObj(Object tmpObj, GlobalTable* GTable)
{
	uint32 size = (4 * 8) + (32 * 4) + (4 * 1);
	PacketBuffer aBuffer(5000);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, GTable->client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(size);
	aBuffer.write<uint32>(0x66aedd50);
	aBuffer.write<uint32>(0x0000c878);
	aBuffer.write<uint32>(tmpObj.id);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x00038c65); //instance

	aBuffer.write<uint32>(tmpObj.position.x);
	aBuffer.write<uint32>(tmpObj.position.y);
	aBuffer.write<uint32>(tmpObj.position.z);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(tmpObj.rotation.x);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(tmpObj.rotation.y);
	aBuffer.write<uint32>(0);

	aBuffer.write<uint32>(tmpObj.position.x);
	aBuffer.write<uint32>(tmpObj.position.y);
	aBuffer.write<uint32>(tmpObj.position.z);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(tmpObj.rotation.x);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(tmpObj.rotation.y);
	aBuffer.write<uint32>(tmpObj.rotation.z);

	aBuffer.write<uint32>(0x000f696e);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(tmpObj.data0);
	aBuffer.write<uint32>(tmpObj.data1);
	aBuffer.write<uint32>(tmpObj.data2);
	aBuffer.write<uint32>(tmpObj.data3);
	aBuffer.write<uint32>(tmpObj.data4);
	aBuffer.write<uint32>(tmpObj.data5);
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(tmpObj.unk0);
	aBuffer.write<string>(tmpObj.sdat1);
	aBuffer.write<string>(tmpObj.sdat2);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(tmpObj.unk1);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(1);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(GTable->client->clientSocket);
	return true;
}

bool sendPackets::passBlob::objHandle::checkAlreadySpawned(vector<uint32>* spawned, uint32 objId)
{
	if(spawned->size() > 0)
	{
		for(uint32 x = 0; x < spawned->size(); x++)
			if(spawned->at(x) == objId) 
				return true;
	}

	return false;
}
