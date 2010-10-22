/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

void WorldServer::CharStat::sendCharSpawn(GameClient * client)
{
	uint8 pack1[] = {
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 
		0x00,0x00, 0x30, 0xc8, 
		0x01, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0xa4, 0x28, 
		0x00, 0x00, 0x00, 0x00, 
		0x01, 
		0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 

		0x00, 0x00, 0x04, 0x79, //stamina
		0x00, 0x00, 0x43, 0x30, //maxStamina
		0x00, 0x00, 0x08, 0x1c, //mana
		0x00, 0x00, 0x40, 0x74, //maxMana

		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x01, 0x02, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x0a, 
		0x00
	};

	uint8 pack2[] = {
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x64, 
		0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x04, 
		0x00, 0x00, 0x00, 0x02
	};

	uint8 pack3[] = {
		0x00, 0x00, 0x03, 0xf1, 
		0x01, 
							
		0x00, 0x00, 0x00, 0x03, 

		0x00, 0x00, 0x00, 0x05, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x00, 0x00, 0x03, 0xf1, 
		0x00, 0x00, 0x00, 0x10, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x00, 0x00, 0x01, 0x30, 0x41, 0x59, 0x4b, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x00, 0x00, 0x03, 0xf1, 
		0x00, 0x00, 0x00, 0x12, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x01, 0x43, 0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0x00, 0x00, 0x03, 0xf1, 
							
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
							
		0x00, 0x00, 0x00, 0x02, 
		0x00, 0x00, 0x00, 0xb7, 0x3f, 0x80, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 

		0x00, 0x00, 0x00, 0x00, //Hair
		0x3f, 0x80, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 

		//0x00, 0x00, 0x01, 0x4b, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 
		//0x00, 0x00, 0x01, 0xe3, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 
		//0x00, 0x00, 0x01, 0x12, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 
		//0x00, 0x00, 0x01, 0xb9, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 

		0x00, 0x00, 0x37, 0x2e, 
		0x00, 0x00, 0x01, 0x90, 0x00, 
		0x00, 0x00, 0x11, 0x30, 0x00, 
		0x00, 0x00, 0x19, 0x00, 0x00, 
		0x00, 0x00, 0x1a, 0x2c, 0x00, 
		0x00, 0x00, 0x1a, 0x90, 0x00, 
		0x00, 0x00, 0x1a, 0xf4, 0x00, 
		0x00, 0x00, 0x1e, 0x78, 0x00, 
		0x00, 0x00, 0x1f, 0xa4, 0x00, 
		0x00, 0x00, 0x20, 0x71, 0x00, 
		0x00, 0x00, 0x20, 0xd0, 0x00, 
		0x00, 0x00, 0x22, 0xc4, 0x00, 
		0x00, 0x00, 0x35, 0x20, 0x00, 
		0x00, 0x01, 0x98, 0x34, 0x00, 

		0x00, 0x00, 0x00, 0x02, 
		0x00, 0x00, 0x00, 0x6f, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0xf1, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x4a, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 
		0x3e, 0x4f, 0x4f, 0x3c
	};

	PacketBuffer aBuffer(50000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
	uint32 size = sizeof(pack1) + sizeof(pack2) + sizeof(pack3);
	size += (21 * 4) + (8 * 2) + (4 * 1) + 2 + client->charInfo.name.size();
	aBuffer.write<uint32>(size);
	aBuffer.write<uint32>(0xfa015807);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0002);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint16>(0x0041);
	aBuffer.write<uint32>(0x00038c65); //instance id
	aBuffer.write<uint32>(0x00008a88);
	aBuffer.write<uint32>(client->charInfo.position.x);
	aBuffer.write<uint32>(client->charInfo.position.y);
	aBuffer.write<uint32>(client->charInfo.position.z);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x3ef57744);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x3f6803ca);
	aBuffer.write<uint16>(0x0000);
	aBuffer.write<uint8>(getSexRace(client->charInfo.sex, client->charInfo.race));
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>(client->charInfo.name);	//Char-Name
	aBuffer.write<uint32>(0x00000001);	
	aBuffer.write<uint8>(0x04);
	aBuffer.write<uint8>(client->charInfo.Class);
	aBuffer.writeArray(pack1, sizeof(pack1));
	aBuffer.write<uint16>(client->charInfo.level);
	aBuffer.write<uint16>(client->charInfo.level);
	aBuffer.write<uint16>(0x2a94); // maxhealth
	aBuffer.write<uint32>(0x0000319c); //health
	aBuffer.write<uint16>(0x0032);
	aBuffer.write<uint16>(0x0000);
	aBuffer.write<uint32>(client->charInfo.size);
	aBuffer.writeArray(pack2, sizeof(pack2));
	aBuffer.write<uint32>(client->charInfo.headmesh);
	aBuffer.writeArray(pack3, sizeof(pack3));
	aBuffer.doItAll(client->clientSocket);
}

void WorldServer::CharStat::sendCharacterStats(GameClient * client)
{
	Database.getMaxStats(client);
	client->charInfo.InitalizeStatupStats(client);

	uint8 pack1[] = {
		0x00, 
		0x00, 0x00, 0x00, 0x17, 
		0x00, 0x00, 0x00, 0x19, 
		0x00, 0x00, 0x00, 0x03, 
		0x00, 0x00, 0x00, 0x19, 

		0x00, 0x00, 0x0f, 0xc4, // = 4036 -> /1009 = 4
		//Item1
		0x00, 0x00, 0x00, 0x05, 
		0x20, 0xd4, 0x00, 0x07, //Crazy Value
		0x35, 0x76, 0xd4, 0xf5, 0x00, 0x00, 0x0b, 0xd3, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 
		0x00, 0x02, 0x50, 0x4a, 
		0x00, 0x04, 0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x00, 0x00, 0x03, 0xf1,
		//Item2
		0x00, 0x00, 0x00, 0x10, 0x20, 0xd4, 
		0x00, 0x07, //something with part-length of the item
		0x35, 0x76, 0xd4, 0xf4, 
		0x00, 0x00, 0x0b, 0xd3, 
		0x00, 0x00, 0x00, 0x00, 
		0x04, 0x00, 0x00, 0x01, 
		0x00, 0x00, 0x00, 0x17, 
		0x00, 0x02, 0x50, 0x4b, 
		0x00, 0x04, 0xf2, 0x4c, 
		0x00, 0x04, 0xf2, 0x4c, 
		0x00, 0x00, 0x00, 0x01, 
		0x30, 0x41, 0x59, 0x4b, 
		0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 
		0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 
		0x00, 0x00, 0x03, 0xf1,
		//Item3
		0x00, 0x00, 0x00, 0x12, 0x20, 0xd4, 0x00, 0x07, 
		0x35, 0x76, 0xd4, 0xf6, 0x00, 0x00, 0x0b, 0xd3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x17, 0x00, 0x02, 0x50, 0x4c, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x01, 0x43, 0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0x00, 0x00, 0x03, 0xf1,
							
		0x00, 0x00, 0x00, 0x18, 
		0x00, 0x00, 0x00, 0x03, 
		0x00, 0x00, 0x00, 0x18, 
		0x00, 0x00, 0x03, 0xf1, 
		
		0x00, 0x00, 0x00, 0x03, 
		0x00, 0x00, 0x00, 0x30, 
		0x00, 0x00, 0x03, 0xf1, 

		0x00, 0x00, 0x00, 0x03, 
		0x00, 0x00, 0x00, 0x30, 
		0x00, 0x00, 0x03, 0xf1
	};
		
	uint8 pack1_1[] = {
		0x00, 0x00, 0x07, 0xe2 ,

		0x00, 0x00, 0x27, 0xfe, 
		0x00, 0x00, 0x00, 0x01, 
		0x00, 0x00, 0x27, 0xfe, 
		0x00, 0x00, 0x00, 0x01, 
		0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x04, 
		0x00, 0x00, 0x04, 0x12, 
		0x00, 0x65, 
		0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x31, 0x30, 0x32, 0x33, 0x38, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x33, 0x30, 0x31, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x70, 0x5e, 0x5d, 0x2d, 0x59, 0x41, 0x6b, 0x44, 0x62, 0x31, 0x6a, 0x6c, 0x67, 0x73, 0x21, 0x26, 0x61, 0x6d, 0x70, 0x3b, 0x66, 0x48, 0x58, 0x42, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e,
		0x00, 0x61, 
		0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x31, 0x30, 0x32, 0x33, 0x38, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x33, 0x30, 0x32, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x21, 0x63, 0x42, 0x42, 0x77, 0x43, 0x4f, 0x33, 0x2a, 0x28, 0x6e, 0x5f, 0x47, 0x28, 0x2f, 0x56, 0x2f, 0x37, 0x4d, 0x3f, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e,
		0x00, 0x6b,
		0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x32, 0x34, 0x31, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x33, 0x30, 0x30, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x63, 0x78, 0x5e, 0x77, 0x39, 0x37, 0x23, 0x2e, 0x6e, 0x26, 0x23, 0x33, 0x39, 0x3b, 0x39, 0x47, 0x71, 0x26, 0x23, 0x39, 0x32, 0x3b, 0x4e, 0x60, 0x4d, 0x2b, 0x26, 0x23, 0x33, 0x39, 0x3b, 0x5e, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e,
		0x00, 0x66,
		0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x34, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x32, 0x30, 0x30, 0x30, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x71, 0x37, 0x69, 0x5e, 0x79, 0x4b, 0x25, 0x75, 0x44, 0x26, 0x71, 0x75, 0x6f, 0x74, 0x3b, 0x46, 0x6d, 0x4d, 0x60, 0x46, 0x26, 0x61, 0x6d, 0x70, 0x3b, 0x32, 0x69, 0x77, 0x72, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e,
		0x00, 0x64,
		0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x34, 0x30, 0x31, 0x30, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x32, 0x30, 0x30, 0x30, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x34, 0x72, 0x37, 0x4c, 0x64, 0x36, 0x76, 0x44, 0x45, 0x59, 0x34, 0x63, 0x64, 0x55, 0x35, 0x5b, 0x26, 0x23, 0x33, 0x39, 0x3b, 0x76, 0x33, 0x2f, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e,
		0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xf1, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x96, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0b, 0xd3, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09
	};

	uint8 pack1_2[] = {
		0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x05, 0xa8, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x05, 0xa8, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xb4, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x01, 0xa0, 0x00, 0x00, 0x02, 0xbd, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x31, 0x34, 0x34, 0x38, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x33, 0x30, 0x34, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x70, 0x41, 0x52, 0x41, 0x54, 0x58, 0x31, 0x21, 0x44, 0x58, 0x5d, 0x26, 0x23, 0x39, 0x32, 0x3b, 0x53, 0x6f, 0x2b, 0x30, 0x2a, 0x41, 0x39, 0x42, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e, 0x00, 0x4a, 0x3c, 0x6c, 0x6f, 0x63, 0x61, 0x6c, 0x69, 0x7a, 0x65, 0x64, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x31, 0x38, 0x30, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x31, 0x32, 0x30, 0x30, 0x22, 0x20, 0x66, 0x6c, 0x61, 0x67, 0x73, 0x3d, 0x22, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x62, 0x32, 0x59, 0x32, 0x5d, 0x2c, 0x4c, 0x66, 0x56, 0x66, 0x41, 0x56, 0x70, 0x73, 0x2a, 0x39, 0x4a, 0x58, 0x2a, 0x47, 0x22, 0x20, 0x3e, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0c
	};

	uint8 pack1_3[] = {
		0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x0d, 0xa2, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x0d, 0xa2, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x11, 0xd3, 0x36, 0x31, 0x4c, 0x56, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x02, 0x09, 0x00, 0x00, 0x02, 0xf5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x3c, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x33, 0x34, 0x39, 0x30, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x30, 0x33, 0x30, 0x34, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x63, 0x41, 0x21, 0x28, 0x45, 0x62, 0x50, 0x70, 0x66, 0x7a, 0x55, 0x4b, 0x26, 0x23, 0x39, 0x32, 0x3b, 0x57, 0x50, 0x4e, 0x40, 0x21, 0x48, 0x21, 0x22, 0x20, 0x6b, 0x6e, 0x75, 0x62, 0x6f, 0x74, 0x3d, 0x22, 0x30, 0x22, 0x20, 0x20, 0x3e, 0x3c, 0x2f, 0x72, 0x65, 0x6d, 0x6f, 0x74, 0x65, 0x66, 0x6f, 0x72, 0x6d, 0x61, 0x74, 0x3e, 0x00, 0x50, 0x3c, 0x6c, 0x6f, 0x63, 0x61, 0x6c, 0x69, 0x7a, 0x65, 0x64, 0x20, 0x69, 0x64, 0x3d, 0x22, 0x39, 0x35, 0x30, 0x30, 0x22, 0x20, 0x63, 0x61, 0x74, 0x65, 0x67, 0x6f, 0x72, 0x79, 0x3d, 0x22, 0x35, 0x31, 0x30, 0x30, 0x30, 0x22, 0x20, 0x66, 0x6c, 0x61, 0x67, 0x73, 0x3d, 0x22, 0x6d, 0x22, 0x20, 0x6b, 0x65, 0x79, 0x3d, 0x22, 0x5d, 0x2b, 0x2f, 0x73, 0x55, 0x4b, 0x26, 0x23, 0x33, 0x39, 0x3b, 0x47, 0x33, 0x6d, 0x4f, 0x29, 0x63, 0x6c, 0x46, 0x60, 0x29, 0x39, 0x4a, 0x68, 0x22, 0x20, 0x3e, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xf1, 0x57, 0x36, 0x51, 0x54, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x30, 0x51, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x30, 0x55, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x28, 0x50, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x30, 0x53, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x28, 0x2e, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x28, 0x2f, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x2a, 0xa3, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x01
	};

	uint8 pack1_4[] = {
		0x00, 0x00, 0x03, 0xf1, 

		0x00, 0x00, 0x03, 0xf1, 

		0x00, 0x00, 0x03, 0xf1, 

		0x00, 0x00, 0x00, 0x00, 
							
		0x00, 0x00, 0x46, 0xf2, // (17+1)*1009
							
		0x00, 0x00, 0x02, 0x52, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x02, 0x53, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x02, 0x54, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x02, 0x55, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 
		0x00, 0x00, 0x00, 0xe0, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0xae, 0x00, 0x00, 0x00, 0x02, 
		0x00, 0x00, 0x00, 0xad, 0x00, 0x00, 0x00, 0x02, 
		0x00, 0x00, 0x00, 0xbf, 
		//0x00, 0x72, 0x7b, 0xc7, //instance???
		0x00, 0x03, 0x8c, 0x65, 

		0x00, 0x00, 0x04, 0x0b, 0x00, 0x00, 0x00, 0x00
	};

	uint8 pack2[] = {
		0x00, 0x00, 0x00, 0xa9, 0x00, 0x00, 0x00, 0x00, 

		//0x00, 0x01, 0x1f, 0xb9, // (72+1)*1009
		0x00, 0x01, 0x2b, 0x8c,
							
		0x00, 0x00, 0x01, 0x6b, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x4a, 0x00, 0x00, 0x00, 0x00
	};

	uint8 pack3[] = {
		0x00, 0x00, 0x03, 0xbf, 0x00, 0x00, 0x00, 0x32, 
		0x00, 0x00, 0x00, 0xa5, 0x00, 0x00, 0x00, 0x6f, 
		0x00, 0x00, 0x02, 0x41, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x01, 0x13, 0x00, 0x00, 0x00, 0x08
	};

	uint8 pack4[] = {
		0x00, 0x00, 0x00, 0x4f, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x0c, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x3b, 0x00, 0x00, 0x00, 0x03
	};

	uint8 pack6[] = {
		0x00, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x03, 0xe8, 
		0x00, 0x00, 0x00, 0x3a, 0x00, 0x00, 0x00, 0x44, //change on lvl2 sniff
		0x00, 0x00, 0x00, 0x21, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x4b, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0xd7, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x01, 0x85, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x02, 0x5f, 0x59, 0x47, 0xc8, 0x29, //change on lvl2 sniff
		0x00, 0x00, 0x00, 0x6a, 0x00, 0x00, 0x00, 0x14,  
		0x00, 0x00, 0x04, 0x3b, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x45, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x3d, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x3f, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x8e, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x3e, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x3c, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x8c, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x38, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x32, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x30, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x44, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x40, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x43, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x04, 0x41, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x42, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x03, 0x36, 0x00, 0x00, 0x00, 0x00, 

		0x00, 0x00, 0x03, 0x9d, 0x00, 0x00, 0x00, 0x64, 
		0x00, 0x00, 0x03, 0x9b, 0x00, 0x00, 0x00, 0x64, 
		0x00, 0x00, 0x03, 0x98, 0x00, 0x00, 0x00, 0x64, 
		0x00, 0x00, 0x03, 0x9a, 0x00, 0x00, 0x00, 0x64, 

		0x00, 0x00, 0x00, 0x7f, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x5b, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x3c, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x01, 0xc8, 0x00, 0x00, 0x00, 0x00
	};

	string levelSpells = Database.getLevelSpells(client->charInfo.Class, client->charInfo.level);
	vector<string> spellsx;
	if(levelSpells.size() > 0)
	{
				spellsx= String::splitString(levelSpells, "-");
	}
	uint8 pack8[] = {
		0x00, 0x00, 0x00, 0x01, 
		0x01, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
		0x00, 
		0x3e, 0x4f, 0x4f, 0x3c
	};
	
	PacketBuffer aBuffer(50000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
	uint32 size = sizeof(pack1) + sizeof(pack2) + sizeof(pack3) + sizeof(pack4) + sizeof(pack6) + sizeof(pack1_4);
	//size += sizeof(pack1_1) + sizeof(pack1_2) + sizeof(pack1_3) + (2 * 4);
	size += (4 * 4) + (38 * 8) + 1;
	if(levelSpells.size() > 0)
	{
		size += (1*4) + (spellsx.size() * 3 * 4) + sizeof(pack8);
	}
	else
	{
		size += (1*4) + sizeof(pack8);
	}

	aBuffer.write<uint32>(size);
	aBuffer.write<uint32>(0x87e67dad);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.writeArray(pack1, sizeof(pack1));
	/*
	aBuffer.writeArray(pack1_1, sizeof(pack1_1));
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.writeArray(pack1_2, sizeof(pack1_2));
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.writeArray(pack1_3, sizeof(pack1_3));
	//*/
	aBuffer.writeArray(pack1_4, sizeof(pack1_4));

	aBuffer.write<uint64>(addMaxHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addLevel(client->charInfo.level));
	aBuffer.write<uint64>(addExp(client->charInfo.curExp));
	aBuffer.write<uint64>(addMaxExp(client->charInfo.maxStats.exp));
	aBuffer.write<uint64>(addExpOffset(client->charInfo.maxStats.expOffset));
	aBuffer.writeArray(pack2, sizeof(pack2));
	aBuffer.write<uint64>(addPvpExpOffset(0));
	aBuffer.write<uint64>(addPvpMaxExp(3500));
	aBuffer.write<uint64>(addMurderPoints(0));
	aBuffer.writeArray(pack3, sizeof(pack3));
	aBuffer.write<uint64>(addHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addMaxHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addInteligence(client->charInfo.PlayerAttributes.Intelligence));
	aBuffer.write<uint64>(addMana(client->charInfo.maxStats.mana));
	aBuffer.write<uint64>(addMaxMana(client->charInfo.maxStats.mana));
	aBuffer.write<uint64>(addMana(client->charInfo.maxStats.mana));
	aBuffer.write<uint64>(addMaxStamina(client->charInfo.maxStats.stamina));
	aBuffer.write<uint64>(addStamina(client->charInfo.maxStats.stamina));
	aBuffer.write<uint64>(addStrength(client->charInfo.PlayerAttributes.Strength));
	aBuffer.write<uint64>(addConstitution(client->charInfo.PlayerAttributes.Constitution));
	aBuffer.write<uint64>(addDexterity(client->charInfo.PlayerAttributes.Dexterity));
	aBuffer.write<uint64>(addWisdom(client->charInfo.PlayerAttributes.Wisdom));
	aBuffer.writeArray(pack4, sizeof(pack4));
	aBuffer.write<uint64>(addMaxExp(client->charInfo.maxStats.exp));
	aBuffer.write<uint64>(addExpOffset(client->charInfo.maxStats.expOffset));
	aBuffer.write<uint64>(addLevel(1));//client->charInfo.level));
	aBuffer.write<uint64>(addPvpLevel(0));
	aBuffer.write<uint64>(addPvpExp(0));
	aBuffer.write<uint64>(addExp(client->charInfo.curExp));
	aBuffer.write<uint64>(addSkillpoints(0));
	aBuffer.writeArray(pack6, sizeof(pack6));
	aBuffer.write<uint64>(addCoinsHigherPart(0));
	aBuffer.write<uint64>(addCoinsLowerPart(0));
	aBuffer.write<uint64>(addSiegeLose(0));
	aBuffer.write<uint64>(addSiegeWin(0));
	aBuffer.write<uint64>(addCaptureTheSkullLose(0));
	aBuffer.write<uint64>(addCaptureTheSkullWin(0));
	aBuffer.write<uint64>(addAnnihilationLose(0));
	aBuffer.write<uint64>(addAnnihilationWin(0));
	aBuffer.write<uint64>(addDeath(0));
	aBuffer.write<uint64>(addKillingBlows(0));
	aBuffer.write<uint32>(0x000003f1);
	aBuffer.write<uint8>(1);

	//aBuffer.writeArray(pack7, sizeof(pack7));
	if(levelSpells.size() > 0)
	{
		//Spells
		aBuffer.write<uint32>((spellsx.size()+1)*1009);
		for(uint32 i = 0; i < spellsx.size(); i++)
		{
			aBuffer.write<uint32>(atoi64(spellsx[i].c_str()));
			aBuffer.write<uint32>(0xffffffff);
			aBuffer.write<uint32>(0x00000002);
		}
	}
	else
	{
		aBuffer.write<uint32>(1009); // = 0 spells (0x0000013f)
	}
	aBuffer.writeArray(pack8, sizeof(pack8));
	aBuffer.doItAll(client->clientSocket);
}

//Little functions that we doesn't need to know the opcode

uint64 WorldServer::CharStat::addHealth(uint32 value)
{
	return ( 0x0000001b00000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addMaxHealth(uint32 value)
{
	return ( 0x0000000100000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addMana(uint32 value)
{
	return ( 0x000001fa00000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addMaxMana(uint32 value)
{
	return ( 0x000001fb00000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addStamina(uint32 value)
{
	return ( 0x000001f800000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addMaxStamina(uint32 value)
{
	return ( 0x000001f900000000 + ( value * 100 ) );
}

//Attributes
uint64 WorldServer::CharStat::addInteligence(uint32 value)
{
	return ( 0x0000032800000000 + ( value * 10 ) );
}

uint64 WorldServer::CharStat::addStrength(uint32 value)
{
	return ( 0x0000032400000000 + ( value * 10 ) );
}

uint64 WorldServer::CharStat::addConstitution(uint32 value)
{
	return ( 0x0000032600000000 + ( value * 10 ) );
}

uint64 WorldServer::CharStat::addDexterity(uint32 value)
{
	return ( 0x0000032e00000000 + ( value * 10 ) );
}
uint64 WorldServer::CharStat::addWisdom(uint32 value)
{
	return ( 0x0000032a00000000 + ( value * 10 ) );
}
//-------------
uint64 WorldServer::CharStat::addLevel(uint32 value)
{
	return ( 0x0000003600000000 + ( value ) );
}

uint64 WorldServer::CharStat::addExp(uint32 value)
{
	return ( 0x0000003400000000 + ( value ) );
}

uint64 WorldServer::CharStat::addMaxExp(uint32 value)
{
	return ( 0x0000015e00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addExpOffset(uint32 value)
{
	return ( 0x0000003900000000 + ( value ) );
}

uint64 WorldServer::CharStat::addSkillpoints(uint32 value)
{
	return ( 0x000000f300000000 + ( value ) );
}

uint64 WorldServer::CharStat::addPvpLevel(uint32 value)
{
	return ( 0x0000004600000000 + ( value ) );
}

uint64 WorldServer::CharStat::addPvpExp(uint32 value)
{
	return ( 0x0000003300000000 + ( value ) );
}

uint64 WorldServer::CharStat::addPvpExpOffset(uint32 value)
{
	return ( 0x0000016300000000 + ( value ) );
}

uint64 WorldServer::CharStat::addPvpMaxExp(uint32 value)
{
	return ( 0x0000016100000000 + ( value ) );
}

uint64 WorldServer::CharStat::addMurderPoints(uint32 value)
{
	return ( 0x0000044900000000 + ( value * 100 ) );
}

uint64 WorldServer::CharStat::addCoinsHigherPart(uint64 value)
{
	return ( 0x0000014b00000000 + ( value & 0xffffffff00000000 ) );
}

uint64 WorldServer::CharStat::addCoinsLowerPart(uint64 value)
{
	return ( 0x0000003d00000000 + ( value & 0x00000000ffffffff ) );
}

uint64 WorldServer::CharStat::addSiegeLose(uint32 value)
{
	return ( 0x0000029f00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addSiegeWin(uint32 value)
{
	return ( 0x0000029e00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addCaptureTheSkullLose(uint32 value)
{
	return ( 0x0000029d00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addCaptureTheSkullWin(uint32 value)
{
	return ( 0x0000029c00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addAnnihilationLose(uint32 value)
{
	return ( 0x0000029b00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addAnnihilationWin(uint32 value)
{
	return ( 0x0000029a00000000 + ( value ) );
}

uint64 WorldServer::CharStat::addDeath(uint32 value)
{
	return ( 0x0000029200000000 + ( value ) );
}

uint64 WorldServer::CharStat::addKillingBlows(uint32 value)
{
	return ( 0x0000029000000000 + ( value ) );
}

uint8 WorldServer::CharStat::getSexRace(uint32 sex, uint32 race)
{
	switch(sex)
	{
	case 0x02:	//male
		{
			switch(race)
			{
			case 0x01:	//Aquilonia
				{
					return 0x06;
					break;
				}
				
			case 0x02:	//Cimmerian
				{
					return 0x08;
					break;
				}
			case 0x03:	//Stygian
				{
					return 0x0e;
					break;
				}
			}
			break;
		}
	case 0x03:	//female
		{
			switch(race)
			{
			case 0x01:	//Aquilonia
				{
					return 0x07;
					break;
				}
				
			case 0x02:	//Cimmerian
				{
					return 0x0b;
					break;
				}
			case 0x03:	//Stygian
				{
					return 0x0f;
					break;
				}
			}
			break;
		}
	}
}

void WorldServer::CharStat::setStartStats(GameClient * client)
{
	PacketBuffer aBuffer(50000);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
	aBuffer.write<uint32>(0x00000045);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x00000006); 
	aBuffer.write<uint64>(addMaxHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addHealth(client->charInfo.maxStats.health));
	aBuffer.write<uint64>(addStamina(client->charInfo.maxStats.stamina));
	aBuffer.write<uint64>(addMaxStamina(client->charInfo.maxStats.stamina));
	aBuffer.write<uint64>(addMana(client->charInfo.maxStats.mana));
	aBuffer.write<uint64>(addMaxMana(client->charInfo.maxStats.mana));
	aBuffer.doItAll(client->clientSocket);
}

void WorldServer::CharStat::setItemStats(GameClient* client, uint32 placeId, uint32 itemId)
{
	PacketBuffer aBuffer(50000);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
	aBuffer.write<uint32>(0x00000065);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(placeId);
	aBuffer.write<uint32>(0x00000017); 
	aBuffer.write<uint32>(itemId);
	aBuffer.doItAll(client->clientSocket);
}