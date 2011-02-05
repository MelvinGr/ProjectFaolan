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

void PassBlob::PassBlobHandler(Packet* packet, GlobalTable* GTable) //GameClient* client)
{
	GameClient* client = GTable->client;

	uint32 length = packet->data->read<uint32>();
	uint32 passblobOpcode = packet->data->read<uint32>();

	switch(passblobOpcode)
	{
		case PassBlobOpcodes::LOGOUT:
			{
				uint32 unk = packet->data->read<uint32>();
				uint32 accountId = packet->data->read<uint32>();
				accountId &= 0x00ffffff;
				Log.Notice("User with Id %i is now logged out.\n", accountId);
				break;
			}
		case PassBlobOpcodes::CHAR_MOVEMENT:
			{
				CharMovement(packet, client);
				break;
			}

		case PassBlobOpcodes::CHAR_ACTION:
			{
				CharAction(packet, GTable); //client);
				break;
			}

		case PassBlobOpcodes::SELECT_OBJECT:
			{
				SelectObject(packet, client);
				break;
			}

		case PassBlobOpcodes::DELETE_ITEM:
			{
				DeleteItem(packet, client);
				break;
			}

		case PassBlobOpcodes::LOADBAR_FULL:
			{
				Log.Debug("Receive GA_PassBlob - LOADBAR_FULL\n");

				//At char creation there is no character and so level is 0
				if(client->charInfo.level > 0)
				{
					//At first enter world there doesn't exists an entry for the player items
					if(Database.checkItemTable(client->charInfo.characterID))
					{
						//normal enter world
						enterWorld(packet, client);
					}
					else
					{
						//First enter world
						firstEnterWorld(packet, client);
					}
				}
				else
				{
					//Char creation
					enterCharCreation(packet, client);
				}
				break;
			}

		case 0xcffd0519:
			{
				Log.Debug("Receive GA_PassBlob - Unknown passblob-opcode 0x%08x\n", passblobOpcode);

				uint32 data = packet->data->read<uint32>();
				uint32 ClientInst = packet->data->read<uint32>();
				uint8 unk0 = packet->data->read<uint8>();
				uint32 arrows = packet->data->read<uint32>();
				uint32 end = packet->data->read<uint32>();

				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
				aBuffer.write<uint32>(length);
				aBuffer.write<uint32>(passblobOpcode);
				aBuffer.write<uint32>(data);
				aBuffer.write<uint32>(ClientInst);
				aBuffer.write<uint8>(unk0);
				aBuffer.write<uint32>(arrows);
				aBuffer.write<uint32>(end);
				aBuffer.doItAll(client->clientSocket);
				break;
			}

		case 0x06ec1255:
			{
				Log.Debug("Receive GA_PassBlob - Unknown passblob-opcode 0x%08x\n", passblobOpcode);

				uint32 unk0 = packet->data->read<uint32>();
				uint32 ClientInst = packet->data->read<uint32>();
				uint32 unk1 = packet->data->read<uint16>();
				switch(unk1)
				{
				case 0x0007:
					{
						uint8 pack0[] = {
							0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x44, 0x8f, 0xb6, 0x00, 0x44, 0x8f, 0xb6, 0x00, 0x00, 0x00, 0x01, 0x56, 0x41, 0x34, 0x55, 0xc1, 0x9d, 0x89, 0x94, 0x5a, 0x3f, 0xed, 0xe3, 0x2c, 0xa3, 0x64, 0x95, 0x0b, 0x1f, 0x15, 0x94, 0xc1, 0x9d, 0x89, 0x94, 0x5a, 0x3f, 0xed, 0xe3, 0x2c, 0xa3, 0x64, 0x95, 0x0b, 0x1f, 0x15, 0x94, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xa0, 0xa7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x37, 0xe6, 0xdf, 0x00, 0x37, 0xe6, 0xdf, 0x00, 0x00, 0x00, 0x01, 0x56, 0x4e, 0x36, 0x38, 0xaf, 0x09, 0x5c, 0x95, 0x20, 0x97, 0xfc, 0xba, 0x63, 0xee, 0x82, 0x64, 0x2c, 0x6d, 0xd7, 0x22, 0xaf, 0x09, 0x5c, 0x95, 0x20, 0x97, 0xfc, 0xba, 0x63, 0xee, 0x82, 0x64, 0x2c, 0x6d, 0xd7, 0x22, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x3c, 0x4c, 0xd4, 0x00, 0x3c, 0x4c, 0xd4, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x38, 0x30, 0x52, 0x6e, 0x10, 0x1d, 0xa8, 0x8d, 0x2e, 0x2e, 0xd6, 0x3f, 0x26, 0x5e, 0xb3, 0x0e, 0x99, 0x45, 0xaa, 0x6e, 0x10, 0x1d, 0xa8, 0x8d, 0x2e, 0x2e, 0xd6, 0x3f, 0x26, 0x5e, 0xb3, 0x0e, 0x99, 0x45, 0xaa, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
						};
						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(0x00000112);
						aBuffer.write<uint32>(0x06ec1255);
						aBuffer.write<uint32>(0x0000c350);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.writeArray(pack0, sizeof(pack0));
						aBuffer.doItAll(client->clientSocket);
						break;
					}
				}

				break;
			}

		case  0xa36d3b74:
			{
				Log.Debug("Receive GA_PassBlob - Unknown passblob-opcode 0x%08x\n", passblobOpcode);
				uint32 data = packet->data->read<uint32>();
				uint32 ClientInst = packet->data->read<uint32>();
				uint32 unk = packet->data->read<uint32>();

				switch(unk)
				{
					case 0x0004020a:
						{
							string command = packet->data->read<string>();
							string value = packet->data->read<string>();

							if(command == "TheNameIs")
							{
								client->charInfo.name = value;

								uint32 size = client->charInfo.name.size() + 2 + (6 * 4) + (1 * 1);

								PacketBuffer aBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(size);
								aBuffer.write<uint32>(0xadce0cda);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x00000003);
								aBuffer.write<uint32>(0x01000000);
								aBuffer.write<string>(client->charInfo.name);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								if(!Database.isValidCharName(client->charInfo.name))
								{
									//TODO add missing return packet
									Log.Warning("Charname is incorrect !\n\n");
									break;
								}

								client->charInfo.level = 1;
								client->charInfo.position.x = 0x43a14000;
								client->charInfo.position.y = 0x43114fb3;
								client->charInfo.position.z = 0x4430399a;

								aBuffer = PacketBuffer(500);						
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(34 + client->charInfo.name.size());
								aBuffer.write<uint32>(0xa36d3b74);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(unk);
								aBuffer.write<string>("NicknameOK");
								aBuffer.write<string>(client->charInfo.name);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack01[] = 
								{ 
									0x00, 
									0x00, 0x00, 0x07, 0xe2, 
									0x00, 0x00, 0xcf, 0xef, 
									0x00, 0x00, 0x00, 0x00, 
									0x00, 0x00, 0x00, 0x05, 
									0x00, 0x00, 0x00, 0x00, 
									0x00, 0x00, 0x00, 0x01, 
									0x00, 0x00, 0x00, 0x00, 
									0x00, 0x00, 0x00, 0x01, 
									0x00, 0x00, 0x00, 0x09, 
									0x00, 0x00, 0x46, 0x37, 
									0x00, 0x00, 0x00, 0x00, 0x00, 
									0x3e, 0x4f, 0x4f, 0x3c 
								};

								aBuffer = PacketBuffer(500);						
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000003e);
								aBuffer.write<uint32>(0xf508f4c1);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack01, sizeof(pack01));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);						
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000002a);
								aBuffer.write<uint32>(0xa36d3b74);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00040202);
								aBuffer.write<string>("CharCreationFinished");
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000019);
								aBuffer.write<uint32>(0xf98e10b3);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x01);
								aBuffer.write<uint32>(0x0000006a);
								aBuffer.write<uint32>(0x00000014);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack02[] = {
									0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0xa1, 0xc5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000004a);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(0x00002dd4);
								aBuffer.writeArray(pack02, sizeof(pack02));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000004a);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(0x00002dd5);
								aBuffer.writeArray(pack02, sizeof(pack02));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000000);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d09);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000000);
								aBuffer.write<uint32>(0x00000008);
								aBuffer.write<uint32>(0xffffffff);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000000);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d0a);
								aBuffer.doItAll(client->clientSocket);

								//PACKET 509
								// A ITEM ???
								uint8 pack03[] = {
									0x00, 0x00, 0x00, 0x00, 0x20, 0xd4, 0x00, 0x07, 0x35, 0x76, 0xd4, 0xf4, 0x00, 0x00, 0x13, 0xb5, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x17, 0x00, 0x02, 0x6d, 0x0a, 0x00, 0x00, 0x00, 0x2e, 0x00, 0x00, 0x00, 0x07, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x00, 0x00, 0x01, 0x30, 0x41, 0x59, 0x4b, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x00
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x1d);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack03, sizeof(pack03));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000001);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d0b);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000001);
								aBuffer.write<uint32>(0x00000008);
								aBuffer.write<uint32>(0xffffffff);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000001);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d0c);
								aBuffer.doItAll(client->clientSocket);

								//PACKET 513
								// A ITEM ???
								uint8 pack04[] = {
									0x00, 0x00, 0x00, 0x01, 0x20, 0xd4, 0x00, 0x07, 0x35, 0x76, 0xd4, 0xf5, 0x00, 0x00, 0x13, 0xb5, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x17, 0x00, 0x02, 0x6d, 0x0c, 0x00, 0x00, 0x00, 0x2e, 0x00, 0x00, 0x00, 0x07, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x00
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x1d);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack04, sizeof(pack04));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000002);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d0d);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000002);
								aBuffer.write<uint32>(0x00000008);
								aBuffer.write<uint32>(0xffffffff);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000002);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d0e);
								aBuffer.doItAll(client->clientSocket);

								//PACKET 517
								// A ITEM ???
								uint8 pack05[] = {
									0x00, 0x00, 0x00, 0x02, 0x20, 0xd4, 0x00, 0x07, 0x35, 0x76, 0xd4, 0xf6, 0x00, 0x00, 0x13, 0xb5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x17, 0x00, 0x02, 0x6d, 0x0e, 0x00, 0x00, 0x00, 0x2e, 0x00, 0x00, 0x00, 0x07, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x01, 0x43, 0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x00
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x1d);
								aBuffer.write<uint32>(0x00000068);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack05, sizeof(pack05));
								aBuffer.doItAll(client->clientSocket);

								//PACKET 518
								uint8 pack06[] = {
									0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0xa4, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x04, 0xf2, 0x4c, 0x00, 0x00, 0x00, 0x01, 0x30, 0x41, 0x59, 0x4b, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x95, 0xa3, 0x9b, 0x79, 0xaa, 0xbc, 0xcc, 0x12, 0x4d, 0x3c, 0x16, 0x57, 0xa1, 0x9f, 0xa0, 0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000008e);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack06, sizeof(pack06));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000065);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000010);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d10);
								aBuffer.doItAll(client->clientSocket);

								//PACKET 520
								uint8 pack07[] = {
									0x00, 0x10, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0xa4, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000008e);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack07, sizeof(pack07));
								aBuffer.doItAll(client->clientSocket);

								//PACKET 521
								uint8 pack08[] = {
									0x00, 0x10, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0xa4, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x12, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x01, 0x43, 0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000008e);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack08, sizeof(pack08));
								aBuffer.doItAll(client->clientSocket);

								//PACKET 522
								uint8 pack09[] = {
									0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0xa4, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x04, 0xf1, 0xe3, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x53, 0x47, 0x4f, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x99, 0x3a, 0x59, 0xbc, 0x02, 0x69, 0x29, 0x13, 0x7a, 0x5f, 0x54, 0xcf, 0x9d, 0x47, 0x3d, 0xa9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000008e);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack09, sizeof(pack09));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000065);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000005);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d12);
								aBuffer.doItAll(client->clientSocket);

								//PACKET 524
								uint8 pack10[] = {
									0x00, 0x04, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0xa4, 0x1b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x12, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x33, 0x4b, 0x15, 0x00, 0x00, 0x00, 0x01, 0x43, 0x55, 0x50, 0x35, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0xdd, 0x60, 0xbf, 0x99, 0x76, 0x09, 0x73, 0xf6, 0x79, 0x49, 0xed, 0xca, 0x8d, 0xed, 0xc7, 0xd2, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000008e);
								aBuffer.write<uint32>(0x642cd3d6);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack10, sizeof(pack10));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19);
								aBuffer.write<uint32>(0x00000065);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00000012);
								aBuffer.write<uint32>(0x00000017);
								aBuffer.write<uint32>(0x0026d14);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack11[] = {
									0x00, 0x00, 0x00, 0x00, 0xf0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack11, sizeof(pack11));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000019);
								aBuffer.write<uint32>(0xf98e10b3);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x00000360);
								aBuffer.write<uint32>(0x00000003);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00000b54);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0000189c);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x000019c8);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00001a2c);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00001a90);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00001af4);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00001bbc);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00001fa4);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0000206c);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0000206d);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0000206e);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0000206f);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00002070);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00002071);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x000020d0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack12[] = {
									0x00, 0x00, 0x00, 0x6c, 0x00, 0x0b, 0xbc, 0x46, 0x00, 0x0b, 0xbc, 0x46, 0x00, 0x00, 0x00, 0x01, 0x4b, 0x4d, 0x39, 0x36, 0xc0, 0xeb, 0xa3, 0x41, 0x80, 0x61, 0x06, 0xf3, 0x12, 0xb8, 0x35, 0x3e, 0xb3, 0xc3, 0x96, 0x42, 0xc0, 0xeb, 0xa3, 0x41, 0x80, 0x61, 0x06, 0xf3, 0x12, 0xb8, 0x35, 0x3e, 0xb3, 0xc3, 0x96, 0x42, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack12, sizeof(pack12));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack13[] = {
									0x00, 0x00, 0x00, 0x6d, 0x00, 0x0b, 0xbc, 0x47, 0x00, 0x0b, 0xbc, 0x47, 0x00, 0x00, 0x00, 0x01, 0x50, 0x44, 0x42, 0x31, 0x64, 0xa7, 0x64, 0xb9, 0x96, 0xb5, 0xca, 0xb8, 0xe4, 0x76, 0x7c, 0x76, 0xa7, 0x0d, 0xe4, 0xe7, 0x64, 0xa7, 0x64, 0xb9, 0x96, 0xb5, 0xca, 0xb8, 0xe4, 0x76, 0x7c, 0x76, 0xa7, 0x0d, 0xe4, 0xe7, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack13, sizeof(pack13));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack14[] = {
									0x00, 0x00, 0x00, 0x70, 0x00, 0x0b, 0xbc, 0x4b, 0x00, 0x0b, 0xbc, 0x4b, 0x00, 0x00, 0x00, 0x01, 0x49, 0x31, 0x4b, 0x4b, 0xcd, 0x42, 0x9c, 0xfd, 0x62, 0xf3, 0x3e, 0x51, 0x4d, 0x49, 0x8e, 0x24, 0xb0, 0x4f, 0x6c, 0xcb, 0xcd, 0x42, 0x9c, 0xfd, 0x62, 0xf3, 0x3e, 0x51, 0x4d, 0x49, 0x8e, 0x24, 0xb0, 0x4f, 0x6c, 0xcb, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack14, sizeof(pack14));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack15[] = {
									0x00, 0x00, 0x04, 0xe2, 0x00, 0x32, 0x9b, 0x83, 0x00, 0x32, 0x9b, 0x83, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x50, 0x4d, 0x58, 0x85, 0x42, 0xef, 0xf0, 0x2c, 0xae, 0x85, 0x4a, 0xc7, 0x0d, 0x86, 0xcb, 0xeb, 0xf0, 0x6e, 0xcb, 0x85, 0x42, 0xef, 0xf0, 0x2c, 0xae, 0x85, 0x4a, 0xc7, 0x0d, 0x86, 0xcb, 0xeb, 0xf0, 0x6e, 0xcb, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack15, sizeof(pack15));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack16[] = {
									0x00, 0x00, 0x01, 0x03, 0x00, 0x38, 0x15, 0xc0, 0x00, 0x38, 0x15, 0xc0, 0x00, 0x00, 0x00, 0x01, 0x4c, 0x35, 0x56, 0x33, 0x64, 0x01, 0x8d, 0x59, 0x5b, 0xb1, 0x00, 0x9d, 0x52, 0x74, 0xc5, 0x57, 0x6d, 0x47, 0x45, 0xd4, 0x64, 0x01, 0x8d, 0x59, 0x5b, 0xb1, 0x00, 0x9d, 0x52, 0x74, 0xc5, 0x57, 0x6d, 0x47, 0x45, 0xd4, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack16, sizeof(pack16));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack17[] = {
									0x00, 0x00, 0x04, 0xe4, 0x00, 0x39, 0xe7, 0x83, 0x00, 0x39, 0xe7, 0x83, 0x00, 0x00, 0x00, 0x01, 0x37, 0x30, 0x39, 0x4c, 0x14, 0xbd, 0x51, 0x26, 0x54, 0x6d, 0xd8, 0x6b, 0x7c, 0x60, 0xbd, 0x85, 0x1f, 0x3f, 0x36, 0xc6, 0x14, 0xbd, 0x51, 0x26, 0x54, 0x6d, 0xd8, 0x6b, 0x7c, 0x60, 0xbd, 0x85, 0x1f, 0x3f, 0x36, 0xc6, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
								aBuffer.writeArray(pack17, sizeof(pack17));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x00003520);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416544);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416541);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416542);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416548);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x004165f1);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x004165ef);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416546);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416545);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x004165f0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416547);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416543);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000016);
								aBuffer.write<uint32>(0xa8bbeb7f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x00416549);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x0003ba4c);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000031);
								aBuffer.write<uint32>(0x6b6fd368);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000000f0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x000503fc);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000001d);
								aBuffer.write<uint32>(0x28d0ac99);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x000007ee);
								aBuffer.write<uint32>(0x0000081c);
								aBuffer.write<uint32>(0x00000415);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack18[] = {
									0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xcf, 0x3d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000003e);
								aBuffer.write<uint32>(0xf508f4c1);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack18, sizeof(pack18));
								aBuffer.doItAll(client->clientSocket);

								client->charInfo.map = 0x00000faa;
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000002f);
								aBuffer.write<uint32>(0x10d27bc0);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0162);
								aBuffer.write<uint32>(0x0000c79c);
								aBuffer.write<uint32>(client->charInfo.map);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 pack19_0[] = {
									0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x62, 0x00, 0x00, 0xc7, 0x9c
								};
								uint8 pack19_1[] = {
									0x00, 0x00, 0x00, 0x13, 0x00, 0x00, 0x9c, 0x50, 0x00, 0x5c, 0x6e, 0xcd, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x43, 0xa1, 0x40, 0x00, 0x43, 0x16, 0x00, 0x00, 0x44, 0x30, 0x39, 0x9a, 0x00, 0x00, 0x00, 0x32, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000006f);
								aBuffer.write<uint32>(0x5a32f0d7);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack19_0, sizeof(pack19_0));
								aBuffer.write<uint32>(client->charInfo.map);
								aBuffer.write<uint32>(0);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(pack19_1, sizeof(pack19_1));
								aBuffer.doItAll(client->clientSocket);
								
								Database.updateCharacter(&client->charInfo);
								

								uint32 i_nDimID = (client->nClientInst >> 24) & 0x000000ff;
								RealmInfo* realm = 0;
								for(uint32 i = 0; i < Settings.realms.size(); i++)
								{
									if(Settings.realms[i]->realmID == i_nDimID)
									{
										realm = Settings.realms[i];
										break; // break forloop
									}
								}
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x02);
								aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); //World-Server Adress
								aBuffer.write<uint16>(realm->worldServerPort); //WorldServer Port
								aBuffer.doItAll(client->clientSocket);

								Log.Debug("All Char-Creation Packets sent\n");
							}

							break;
						}
						
					case 0x0004020b:
						{
							uint32 unk2 = packet->data->read<uint32>();
							string command = packet->data->read<string>();
							string value = packet->data->read<string>();
							
							Log.Debug("Get command: %s\n", command.c_str());

							uint32 size = value.length() + 2 + (6 * 4) + (2 * 1);

							PacketBuffer aBuffer(1000);
							aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
							aBuffer.write<uint32>(size);
							aBuffer.write<uint32>(0xbadf5a4b);
							aBuffer.write<uint32>(0x0000c350);
							aBuffer.write<uint32>(client->nClientInst);
							aBuffer.write<uint8>(0);
							aBuffer.write<string>(value);
							aBuffer.write<uint32>(0x3f800000);
							aBuffer.write<uint8>(0);
							aBuffer.write<uint32>(client->counter);
							aBuffer.write<uint32>(0x3e4f4f3c);
							aBuffer.doItAll(client->clientSocket);

							client->counter++;

							if(client->counter >= 9)
							{
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000018);
								aBuffer.write<uint32>(0xa36d3b74);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x00040001);
								aBuffer.write<uint32>(0x000045f6);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);
							}

							break;
						}

					case 0x00040203:
						{
							uint32 unk2 = packet->data->read<uint32>();
							string command = packet->data->read<string>();
							if(command == "StartScript")
							{
								switch(unk2)
								{
								case 0x00361543:
									{
										PacketBuffer aBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
										aBuffer.write<uint32>(0x0000001d);
										aBuffer.write<uint32>(0x28d0ac99);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint8>(0x00);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x0000081c);
										aBuffer.write<uint32>(0x00000415);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
										aBuffer.write<uint32>(0x00000019);
										aBuffer.write<uint32>(0xf98e10b3);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint8>(0x00);
										aBuffer.write<uint32>(0x00000013);
										aBuffer.write<uint32>(0);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
										aBuffer.write<uint32>(0x00000018);
										aBuffer.write<uint32>(0xa36d3b74);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00040001);
										aBuffer.write<uint32>(0x00361543);
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

										break;
									}

								case 0x00365b0b:
									{
										PacketBuffer aBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
										aBuffer.write<uint32>(0x00000018);
										aBuffer.write<uint32>(0xa36d3b74);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00040001);
										aBuffer.write<uint32>(0x00365b0b);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
										aBuffer.write<uint32>(0x0000001d);
										aBuffer.write<uint32>(0x28d0ac99);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint8>(0x00);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x00000398);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										break;
									}
								}
							}
							if(command == "ChangeSex")
							{
								client->charInfo.sex = unk2;

								uint8 pack1[] = {
									0x00, 0x00, 0x00, 0x30, 0x96, 0xc4, 0x67, 0x40, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x2d, 0xd6, 0x00, 0x00, 0x43, 0x85, 0x59, 0x9a, 0x40, 0xbd, 0x91, 0x69, 0x43, 0x92, 0xcc, 0xcd, 0x02, 0x00, 0x00, 0x00, 0x00, 0x33, 0x3b, 0xbd, 0x2e, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								PacketBuffer aBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.writeArray(pack1, sizeof(pack1));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack2[] = {
									0x00, 0x00, 0x00, 0x30, 0x96, 0xc4, 0x67, 0x40, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x2d, 0xd7, 0x00, 0x00, 0x43, 0x84, 0x29, 0x9a, 0x40, 0xc0, 0x67, 0x24, 0x43, 0x8a, 0x8e, 0x98, 0x02, 0x00, 0x00, 0x00, 0x00, 0x33, 0x3b, 0xbd, 0x2e, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.writeArray(pack2, sizeof(pack2));
								aBuffer.doItAll(client->clientSocket);

								uint8 pack3[] = {
									0x00, 0x00, 0x00, 0x4a, 0x64, 0x2c, 0xd3, 0xd6, 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x2d, 0xd6, 0x00, 0x04, 0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0x4f, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x2c, 0xa5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.writeArray(pack3, sizeof(pack3));
								aBuffer.doItAll(client->clientSocket);
							}
							if(command == "ChangeRace")
							{
								client->charInfo.race = unk2;
							}
							if(command == "ChangeHeadMesh")
							{
								client->charInfo.headmesh = unk2;
							}
							if(command == "ChangeSize")
							{
								client->charInfo.size = unk2;
							}
							if(command == "ChangeClass")
							{
								client->charInfo.Class = unk2;
							}
							if(command == "ChangeVoice")
							{
								client->charInfo.voice = unk2;
							}

							break;
						}

					case 0x00040202:
					case 0x00040704:
						{
							uint16 com_length = packet->data->read<uint16>();
							if(com_length < 0x0020)
							{
								packet->data->offset -= 2;

								string command = packet->data->read<string>();

								if(command == "ClientLoadingDone")
								{
									PacketBuffer aBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
									aBuffer.write<uint32>(0x00000019);
									aBuffer.write<uint32>(0xf98e10b3);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.write<uint8>(0x00);
									aBuffer.write<uint32>(0x00000360);
									aBuffer.write<uint32>(0x00000001);
									aBuffer.write<uint32>(0x3e4f4f3c);
									aBuffer.doItAll(client->clientSocket);

									aBuffer = PacketBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x00000022);
									aBuffer.write<uint32>(0x6b87e316);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.write<uint8>(0x00);
									aBuffer.write<uint32>(0x00000b90);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.write<uint8>(0x01);
									aBuffer.write<uint32>(0);
									aBuffer.write<uint32>(0);
									aBuffer.doItAll(client->clientSocket);
									
									aBuffer = PacketBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x0000001d);
									aBuffer.write<uint32>(0x28d0ac99);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.write<uint8>(0x00);
									aBuffer.write<uint32>(0x00000000);
									aBuffer.write<uint32>(0x0000081c);
									aBuffer.write<uint32>(0x00000415);
									aBuffer.write<uint32>(0x3e4f4f3c);
									aBuffer.doItAll(client->clientSocket);

								}
								if(command == "Move")
								{
									//unk what to send
									//06_01_2010_Packet 475
									uint8 pack1[] = { 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0xa1, 0xc5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c };
									PacketBuffer aBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x0000004a);
									aBuffer.write<uint32>(0x642cd3d6);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(0x00069ce0);
									aBuffer.writeArray(pack1, sizeof(pack1));
									aBuffer.doItAll(client->clientSocket);

									//06_01_2010_Packet 476
									uint8 pack2[] = { 0x00, 0x00, 0x43, 0xa6, 0x99, 0x9a, 0x43, 0x12, 0x4b, 0xf2, 0x44, 0x30, 0x81, 0xaa, 0x04, 0x01, 0x00, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x02, 0x43, 0xa6, 0x99, 0x9a, 0x43, 0x12, 0x4b, 0xf2, 0x44, 0x30, 0x81, 0xaa, 0x43, 0xa2, 0x1e, 0x7a, 0x43, 0x11, 0x64, 0x7f, 0x44, 0x30, 0x45, 0x4f, 0x00, 0x41, 0x10, 0xe7, 0xfb, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x43, 0xa6, 0x99, 0x9a, 0x43, 0x12, 0x4b, 0xf2, 0x44, 0x30, 0x81, 0xaa, 0x00, 0x01, 0x3e, 0x4f, 0x4f, 0x3c };
									aBuffer = PacketBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x0000005c);
									aBuffer.write<uint32>(0x96c46740);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(0x00069ce0);
									aBuffer.writeArray(pack2, sizeof(pack2));
									aBuffer.doItAll(client->clientSocket);
								}
								if(command == "IntroDone")
								{
									//unk what to send
									if(client->charInfo.level > 0)
									{
										//06_01_2010_Packet 628
										PacketBuffer aBuffer(500);
										aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
										aBuffer.write<uint32>(0x00000022);
										aBuffer.write<uint32>(0xa36d3b74);
										aBuffer.write<uint32>(0x0000c350);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00040202);
										aBuffer.write<string>("StartFixed3d");
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										//06_01_2010_Packet 629
										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19); 
										aBuffer.write<uint32>(0x00000068);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x00000017);
										aBuffer.write<uint32>(0x004f66ba);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										//06_01_2010_Packet 630
										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19); 
										aBuffer.write<uint32>(0x00000068);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x00000008);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										//06_01_2010_Packet 631
										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x19); 
										aBuffer.write<uint32>(0x00000068);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.write<uint32>(0x00000000);
										aBuffer.write<uint32>(0x00000017);
										aBuffer.write<uint32>(0x004f66ba);
										aBuffer.write<uint32>(0x3e4f4f3c);
										aBuffer.doItAll(client->clientSocket);

										//06_01_2010_Packet 632
										uint8 pack01[] = { 0x00, 0x00, 0x00, 0x00, 0x20, 0xd4, 0x00, 0x05, 0xa1, 0xef, 0xf5, 0x7b, 0x00, 0x00, 0x13, 0xb5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17, 0x00, 0x4f, 0x66, 0xbb, 0x00, 0x00, 0x00, 0x2e, 0x00, 0x00, 0x00, 0x0f, 0x00, 0x03, 0x4a, 0x9f, 0x00, 0x03, 0x4a, 0x9f, 0x00, 0x00, 0x00, 0x01, 0x4f, 0x4f, 0x49, 0x39, 0x0d, 0x65, 0x8e, 0xf7, 0x55, 0x07, 0xbd, 0x3f, 0x00, 0x8d, 0xca, 0x94, 0xe8, 0xaa, 0x4d, 0xb1, 0x0d, 0x65, 0x8e, 0xf7, 0x55, 0x07, 0xbd, 0x3f, 0x00, 0x8d, 0xca, 0x94, 0xe8, 0xaa, 0x4d, 0xb1, 0x00, 0x00, 0x03, 0xf1, 0x00, 0x00, 0x00, 0x00 };
										aBuffer = PacketBuffer(500);
										aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x1d); 
										aBuffer.write<uint32>(0x00000068);
										aBuffer.write<uint32>(client->nClientInst);
										aBuffer.writeArray(pack01, sizeof(pack01));
										aBuffer.doItAll(client->clientSocket);
									}

								}
							}
							else
							{
								if(client->charInfo.map == 4010)
								{
									uint8 test1[] = { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xf1, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x30, 0x51, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x30, 0x55, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x28, 0x50, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x30, 0x53, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x28, 0x2e, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x28, 0x2f, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x2a, 0xa3 };
									PacketBuffer aBuffer(2000);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x000000d8);
									aBuffer.write<uint32>(0x504b7924);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.writeArray(test1, sizeof(test1));
									aBuffer.write<string>("<remoteformat id=\"241\" category=\"50300\" key=\"cx^w97#.n&#39;9Gq&#92;N`M+&#39;^\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<uint32>(0x00000001);
									aBuffer.write<uint32>(0x00000000);
									aBuffer.write<uint16>(0x0000);
									aBuffer.write<uint32>(0x3e4f4f3c);
									aBuffer.doItAll(client->clientSocket);

									uint8 test2_0[] = { 
										0x01, 
										0x00, 0x00, 0x00, 0x01, 
										0x00, 0x00, 0x27, 0xfe, 
										0x00, 0x00, 0x00, 0x01, 
										0x00, 0x00, 0x27, 0xfe, 
										0x00, 0x00, 0x00, 0x01, 
										0x00, 0x00, 0x00, 0x00, 
										0x00, 0x00, 0x00, 0x04, 
										0x00, 0x00, 0x04, 0x12 
									};
									uint8 test2_1[] = { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xf1, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xaf, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0b, 0xd3, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x09, 0x01, 0x07, 0xa0, 0xf0, 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x05, 0xa8, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x05, 0xa8, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xb4 };
									uint8 test2_2[] = { 0x00, 0x00, 0x01, 0xa0, 0x00, 0x00, 0x02, 0xbd, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
									uint8 test2_3[] = { 0x00, 0x00, 0x27, 0xfe, 0x00, 0x00, 0x0d, 0xa2, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x0d, 0xa2, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x11, 0xd3, 0x36, 0x31, 0x4c, 0x56, 0x00, 0x00, 0x00, 0x00 };
									uint8 test2_4[] = { 0x00, 0x00, 0x02, 0x09, 0x00, 0x00, 0x02, 0xf5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

									aBuffer = PacketBuffer(5000);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x00000489);
									aBuffer.write<uint32>(0x338f583c);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.writeArray(test2_0, sizeof(test2_0));
									aBuffer.write<string>("<remoteformat id=\"10238\" category=\"50301\" key=\"p^]-YAkDb1jlgs!&amp;fHXB\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<remoteformat id=\"10238\" category=\"50302\" key=\"!cBBwCO3*(n_G(/V/7M?\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<remoteformat id=\"241\" category=\"50300\" key=\"cx^w97#.n&#39;9Gq&#92;N`M+&#39;^\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<remoteformat id=\"4\" category=\"52000\" key=\"q7i^yK%uD&quot;FmM`F&amp;2iwr\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<remoteformat id=\"4010\" category=\"52000\" key=\"4r7Ld6vDEY4cdU5[&#39;v3/\" knubot=\"0\"  ></remoteformat>");
									aBuffer.writeArray(test2_1, sizeof(test2_1));
									aBuffer.write<uint32>(0x00000faa);	//mapid?
									aBuffer.writeArray(test2_2, sizeof(test2_2));
									aBuffer.write<string>("<remoteformat id=\"1448\" category=\"50304\" key=\"pARATX1!DX]&#92;So+0*A9B\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<localized id=\"180\" category=\"51200\" flags=\"\" key=\"b2Y2],LfVfAVps*9JX*G\" >");
									aBuffer.write<uint32>(0x00000001);
									aBuffer.write<uint32>(0x0000000c);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.writeArray(test2_3, sizeof(test2_3));
									aBuffer.write<uint32>(0x00000faa);	//mapid?
									aBuffer.writeArray(test2_4, sizeof(test2_4));
									aBuffer.write<string>("<remoteformat id=\"3490\" category=\"50304\" key=\"cA!(EbPpfzUK&#92;WPN@!H!\" knubot=\"0\"  ></remoteformat>");
									aBuffer.write<string>("<localized id=\"9500\" category=\"51000\" flags=\"m\" key=\"]+/sUK&#39;G3mO)clF`)9Jh\" >");
									aBuffer.write<uint8>(0x00);
									aBuffer.write<uint32>(0x3e4f4f3c);
									aBuffer.doItAll(client->clientSocket);

									uint8 test3[] = { 0x00, 0x00, 0x43, 0xa1, 0x40, 0x00, 0x43, 0x11, 0x4f, 0xb3, 0x44, 0x30, 0x39, 0x9a, 0x02, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x2b, 0x49, 0x15, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x3e, 0x41, 0x7f, 0x00, 0x3e, 0x4f, 0x4f, 0x3c };
									aBuffer = PacketBuffer(5000);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x00000030);
									aBuffer.write<uint32>(0x96c46740);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.writeArray(test3, sizeof(test3));
									aBuffer.doItAll(client->clientSocket);

									uint8 test4[] = { 0x00, 0x00, 0x43, 0xa6, 0x99, 0x9a, 0x43, 0x12, 0x4b, 0xf2, 0x44, 0x30, 0x81, 0xaa, 0x02, 0x00, 0x00, 0x00, 0x00, 0xbf, 0x3e, 0x41, 0x7f, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x2b, 0x49, 0x15, 0x00, 0x3e, 0x4f, 0x4f, 0x3c };
									aBuffer = PacketBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x00000030);
									aBuffer.write<uint32>(0x96c46740);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(0x00069ce0);	//i think wrong
									aBuffer.writeArray(test4, sizeof(test4));
									aBuffer.doItAll(client->clientSocket);

									aBuffer = PacketBuffer(500);
									aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); 
									aBuffer.write<uint32>(0x0000002d);
									aBuffer.write<uint32>(0xa36d3b74);
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(client->nClientInst);
									aBuffer.write<uint32>(0x00040206);
									aBuffer.write<string>("ThisIsKalanthes");
									aBuffer.write<uint32>(0x0000c350);
									aBuffer.write<uint32>(0x00069ce0);	//i think wrong
									aBuffer.write<uint32>(0x3e4f4f3c);
									aBuffer.doItAll(client->clientSocket);
								}
							break;
						}
					}
				}

				break;
			}

		default:
			{
				Log.Error("Receive GA_PassBlob - Unknown passblob-opcode 0x%08x\n", passblobOpcode);
				Log.Notice("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());
				break;
			}
	}
}