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

void PassBlob::DeleteItem(Packet* packet, GameClient* client)
{
	Log.Debug("Receive GA_PassBlob - DELETE_ITEM\n");
	try
	{
		uint32 data = packet->data->read<uint32>();
		uint32 ClientInst = packet->data->read<uint32>();
		uint8 unk0 = packet->data->read<uint8>();
		uint32 data2 = packet->data->read<uint32>(); //0068
		uint32 unk3 = packet->data->read<uint32>();//07e2
		uint32 unk1 = packet->data->read<uint32>();//004
		uint32 unk2 = packet->data->read<uint32>();//1185 
		uint32 unk4 = packet->data->read<uint32>();//1185
		uint32 end = packet->data->read<uint32>();
		uint32 length = packet->data->bufferLength -4;

		PacketBuffer aBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(length);
		aBuffer.write<uint32>(DELETE_ITEM);
		aBuffer.write<uint32>(data);
		aBuffer.write<uint32>(ClientInst);
		aBuffer.write<uint8>(unk0);
		aBuffer.write<uint32>(data2);
		aBuffer.write<uint32>(unk3);
		aBuffer.write<uint32>(unk1);
		aBuffer.write<uint32>(unk2);
		aBuffer.write<uint32>(unk4);
		aBuffer.write<uint32>(end);
		aBuffer.doItAll(client->clientSocket);

		Log.Debug("Sent GI_PassBlob - DELETE_ITEM\n");
	}
	catch(char* errMsg)
	{
		Log.Error("Error at receiving delete item datas @ DeleteItem.cpp\nError Message:\n%s\n", errMsg);
	}
}