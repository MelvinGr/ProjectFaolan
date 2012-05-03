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

#include "../WorldServer.h"

void PassBlob::SelectObject(Packet* packet, GameClient* client)
{
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
}