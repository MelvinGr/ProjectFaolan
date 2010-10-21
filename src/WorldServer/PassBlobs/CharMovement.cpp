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

#include "../WorldServer.h"

void PassBlob::CharMovement(Packet* packet, GameClient* client)
{
	try
	{
		uint32 data = packet->data->read<uint32>();
		uint32 nClientInst = packet->data->read<uint32>();
		uint16 type = packet->data->read<uint16>();
		client->charInfo.position.x = packet->data->read<uint32>();
		client->charInfo.position.y = packet->data->read<uint32>();
		client->charInfo.position.z = packet->data->read<uint32>();
		uint32 unk_data1 = packet->data->read<uint32>();
		uint32 unk_data2 = packet->data->read<uint32>();

		type &= 0x00ff;

		switch(type)
		{
			case PassBlob::MOVE_WALK:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_WALK\n");
					try
					{
						uint8 unk1 = packet->data->read<uint8>();
						uint32 unk2 = packet->data->read<uint32>();
						uint32 unk3 = packet->data->read<uint32>();
						uint32 unk4 = packet->data->read<uint32>();
						uint32 unk5 = packet->data->read<uint32>();
						uint32 unk6 = packet->data->read<uint32>();
						uint16 unk7 = packet->data->read<uint16>();

						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_WALK);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						aBuffer.write<uint8>(unk1);
						aBuffer.write<uint32>(unk2);
						aBuffer.write<uint32>(unk3);
						aBuffer.write<uint32>(unk4);
						aBuffer.write<uint32>(unk5);
						aBuffer.write<uint32>(unk6);
						aBuffer.write<uint16>(unk7);
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);

						client->charInfo.state = WALKING;
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_WALK - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}

			case PassBlob::MOVE_ROTATECAM:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_ROTATECAM\n");
					try
					{
						uint8 unk1 = packet->data->read<uint8>();
						uint32 unk2 = packet->data->read<uint32>();
						uint32 unk3 = packet->data->read<uint32>();
						uint16 unk4 = packet->data->read<uint16>();

						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_ROTATECAM);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						aBuffer.write<uint8>(unk1);
						aBuffer.write<uint32>(unk2);
						aBuffer.write<uint32>(unk3);
						aBuffer.write<uint16>(unk4);
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_ROTATECAM - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
			case PassBlob::MOVE_JUMP:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_JUMP\n");
					try
					{
						uint8 unk1 = packet->data->read<uint8>();
						uint32 unk2 = packet->data->read<uint32>();
						uint32 unk3 = packet->data->read<uint32>();
						uint32 unk4 = packet->data->read<uint32>();
						uint32 unk5 = packet->data->read<uint32>();
						uint32 unk6 = packet->data->read<uint32>();
						uint32 unk7 = packet->data->read<uint32>();
						uint32 unk8 = packet->data->read<uint32>();
						uint32 unk9 = packet->data->read<uint32>();
						uint16 unk10 = packet->data->read<uint16>();

						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_JUMP);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						aBuffer.write<uint8>(unk1);
						aBuffer.write<uint32>(unk2);
						aBuffer.write<uint32>(unk3);
						aBuffer.write<uint32>(unk4);
						aBuffer.write<uint32>(unk5);
						aBuffer.write<uint32>(unk6);
						aBuffer.write<uint32>(unk7);
						aBuffer.write<uint32>(unk8);
						aBuffer.write<uint32>(unk9);
						aBuffer.write<uint16>(unk10);
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_JUMP - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
			case PassBlob::MOVE_RUN:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_RUN\n");
					try
					{
						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_RUN);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						
						uint32 size = packet->data->bufferLength - ((9 * 4) + 2 + 4);
						aBuffer.writeArray(packet->data->readArray(size), size);
						
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);

						client->charInfo.state = SPRINTING;
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_RUN - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
			case PassBlob::MOVE_RUNJUMP:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_RUNJUMP\n");
					try
					{
						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_RUNJUMP);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						
						uint32 size = packet->data->bufferLength - ((9 * 4) + 2 + 4);
						aBuffer.writeArray(packet->data->readArray(size), size);
						
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_RUNJUMP - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
			case PassBlob::MOVE_SWIM:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - MOVE_SWIM\n");
					try
					{
						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(PassBlob::MOVE_SWIM);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						
						uint32 size = packet->data->bufferLength - ((9 * 4) + 2 + 4);
						aBuffer.writeArray(packet->data->readArray(size), size);
						
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_SWIM - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
			default:
				{
					Log.Debug("Receive GA_PassBlob - CHAR_MOVEMENT - Unknown Type: 0x%04x\n", type);
					try
					{
						PacketBuffer aBuffer(500);
						aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
						aBuffer.write<uint32>(packet->data->bufferLength - 4);
						aBuffer.write<uint32>(CHAR_MOVEMENT);
						aBuffer.write<uint32>(data);
						aBuffer.write<uint32>(client->nClientInst);
						aBuffer.write<uint16>(type);
						aBuffer.write<uint32>(client->charInfo.position.x);
						aBuffer.write<uint32>(client->charInfo.position.y);
						aBuffer.write<uint32>(client->charInfo.position.z);
						aBuffer.write<uint32>(unk_data1);
						aBuffer.write<uint32>(unk_data2);
						
						uint32 size = packet->data->bufferLength - ((9 * 4) + 2 + 4);
						aBuffer.writeArray(packet->data->readArray(size), size);
						
						aBuffer.write<uint32>(0x3e4f4f3c);
						aBuffer.doItAll(client->clientSocket);

						client->charInfo.state = NONE;
					}
					catch(char* errMsg)
					{
						Log.Error("Error at receiving MovementDatas @ MOVE_SWIM - CharMovement.cpp\nError Message:\n%s\n", errMsg);
					}
					break;
				}
		}
	}
	catch(char* errMsg)
	{
		Log.Error("Error at receiving MovementDatas @ CharMovement.cpp\nError Message:\n%s\n", errMsg);
	}
}