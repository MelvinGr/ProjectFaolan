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

void PassBlob::enterWorld(Packet* packet, GameClient* client)
{
	Log.Debug("Enter World\n");

	//Test opcode 0x21 sniff lvl2 PACKET 341
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x21);
	aBuffer.write<uint32>(client->charInfo.position.x);
	aBuffer.write<uint32>(client->charInfo.position.y);
	aBuffer.write<uint32>(client->charInfo.position.z);
	aBuffer.write<uint32>(0x000007e2);
	aBuffer.write<uint32>(client->charInfo.position.x);
	aBuffer.write<uint32>(client->charInfo.position.y);
	aBuffer.write<uint32>(client->charInfo.position.z);
	aBuffer.write<uint32>(0x0000c7a0);
	aBuffer.write<uint32>(client->charInfo.map);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0x000bf76c);
	aBuffer.write<uint32>(0x0000000a);
	aBuffer.write<uint8>(0);
	aBuffer.doItAll(client->clientSocket);

	//Check Abilitys and Spells
	WorldServer::checkAbility(client);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000012);
	aBuffer.write<uint32>(0x06ec1255);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0009);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000011);
	aBuffer.write<uint32>(0x864cfef8);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000019);
	aBuffer.write<uint32>(0xf98e10b3);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00000003);
	aBuffer.write<uint8>(0x60);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000026);
	aBuffer.write<uint32>(0x6b87e316);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x00000b90);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(1);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x28d0ac99);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x000008f2);
	aBuffer.write<uint32>(0x00000424);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000022);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040202);
	aBuffer.write<string>("StartFixed3d");
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000018);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040001);
	aBuffer.write<uint32>(0x0002f1bb);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
}

void PassBlob::firstEnterWorld(Packet* packet, GameClient* client)
{
	Log.Debug("First enter World\n");

	uint8 testing1[] = { 
			0x43, 0xa1, 0x40, 0x00, 
			0x43, 0x11, 0x4f, 0xb3, 
			0x44, 0x30, 0x39, 0x9a, 0x00, 0x00, 0x07, 0xe2, 0x43, 0xa8, 0x27, 0x8d, 0x43, 0x14, 0xf7, 0x8d, 0x44, 0x25, 0x3d, 0xf4, 0x00, 0x00, 0xc7, 0xa0, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0b, 0xf7, 0x6c, 0x00, 0x00, 0x00, 0x0a, 0x00 
	};
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x21); 
	aBuffer.writeArray(testing1, sizeof(testing1));
	aBuffer.doItAll(client->clientSocket);

	//Check Abilitys and Spells
	WorldServer::checkAbility(client);

	//PACKET 341
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000012);
	aBuffer.write<uint32>(0x06ec1255);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0009);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET342
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000011);
	aBuffer.write<uint32>(0x864cfef8);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET344
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x28d0ac99);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000302);
	aBuffer.write<uint32>(0x0000082a);
	aBuffer.write<uint32>(0x00000424);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
							/*
							//PACKET343
							aBuffer = PacketBuffer(500);
							aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
							aBuffer.write<uint32>(0x0000001d);
							aBuffer.write<uint32>(0x96b8dc59);
							aBuffer.write<uint32>(0x0000c350);
							aBuffer.write<uint32>(client->nClientInst);
							aBuffer.write<uint8>(0x00);
							aBuffer.write<uint32>(0x00000001);
							aBuffer.write<uint32>(0x0000001b);
							aBuffer.write<uint32>(0x00002a30);
							aBuffer.write<uint32>(0x3e4f4f3c);
							aBuffer.doItAll(client->clientSocket);
							//*/
	//Play Intro
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000018);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040001);
	aBuffer.write<uint32>(0x00005dff);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(5000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000023c);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x0004060e);
	aBuffer.write<string>("<remoteformat token=\"playorskip(100)|onclientconnected(1)|character_creation Tortage Beach (server-side)\" category=\"50023\" key=\"*$!0mcl$Z!5Ii0UG!TV?\" knubot=\"0\"  ></remoteformat>");
	aBuffer.write<string>("1010003:3147889;1010003:3147890;1010003:3147891;1010003:3147892;1010003:3147893;1010003:3147894;1010003:137283;1010003:202270;1010003:210528;1010003:205370;1010003:3147896;1010003:3147895;1010003:3147897;1010003:3147898;1010003:3147899;1010003:3147900;1010003:208235;1010003:3203880;1010003:3203879;1010003:3203881;1010003:3203876;1010003:3203877;1010003:3203878");
	aBuffer.write<uint32>(0x00b19cf6);
	aBuffer.write<uint32>(0x013659d0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000018);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040001);
	aBuffer.write<uint32>(0x0002f1bb);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000025);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040202);
	aBuffer.write<string>("WelcomeTutorial");
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
}

void PassBlob::enterCharCreation(Packet* packet, GameClient* client)
{
	Log.Debug("Enter Char Creation\n");

	uint8 pack1[] = { 0x43, 0xa7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4c, 0xcd, 0x00, 0x00, 0x07, 0xe2, 0x43, 0xa7, 0x40, 0x00, 0x43, 0x14, 0x66, 0x66, 0x44, 0x25, 0x4c, 0xcd, 0x00, 0x00, 0xc7, 0x9e, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00 };
	uint8 pack2[] = { 0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xd0, 0x0c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x39, 0xbf, 0x93 };
	uint8 pack3[] = { 0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xd0, 0x0c, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x01, 0x9d, 0xd1 };

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x21); 
	aBuffer.writeArray(pack1, sizeof(pack1));
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000012);
	aBuffer.write<uint32>(0x06ec1255);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0009);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000011);
	aBuffer.write<uint32>(0x864cfef8);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000008d);
	aBuffer.write<uint32>(0xf508f4c1);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.writeArray(pack2, sizeof(pack2));
	aBuffer.write<string>("MaleTemplate=50000:11734;FemaleTemplate=50000:11735;StygianSlaver=50000:11736");
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
						
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000008d);
	aBuffer.write<uint32>(0xf508f4c1);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.writeArray(pack3, sizeof(pack3));
	aBuffer.write<string>("MaleTemplate=50000:11734;FemaleTemplate=50000:11735;StygianSlaver=50000:11736");
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x00000020);
	aBuffer.write<uint32>(0xa36d3b74);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00040202);
	aBuffer.write<string>("PlayOrSkip");
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000002c);
	aBuffer.write<uint32>(0xbadf5a4b);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(0x00002dd6);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>("*hairmesh_207920");
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET 385
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000002c);
	aBuffer.write<uint32>(0xbadf5a4b);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(0x00002dd7);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>("*hairmesh_203666");
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET 386
	uint8 pack4[] = {
		0x00, 0x00, 0x00, 0x4a, 0x64, 0x2c, 0xd3, 0xd6, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x2d, 0xd6, 0x00, 0x04, 0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0x4f, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x76, 0xdf, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
	};
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.writeArray(pack4, sizeof(pack4));
	aBuffer.doItAll(client->clientSocket);

	//PACKET 387
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000002d);
	aBuffer.write<uint32>(0xbadf5a4b);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>("*texscript_183875");
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET 388
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000002d);
	aBuffer.write<uint32>(0xbadf5a4b);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(0x00002dd7);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>("*texscript_183875");
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//PACKET 389
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
	aBuffer.write<uint32>(0x0000002d);
	aBuffer.write<uint32>(0xbadf5a4b);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(0x00002dd6);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<string>("*texscript_183875");
	aBuffer.write<uint32>(0x3f800000);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
}