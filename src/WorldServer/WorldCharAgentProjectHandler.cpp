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

void WorldServer::GameCharAgentProjectHandler(Packet* packet, GameClient* client)
{
	Log.Warning("Receive Packet With Opcode: 0x%08X (%s):\n", packet->opcode, packet->receiver.c_str());
	switch(packet->opcode)
	{
	case 0x00:
		{
			uint32 data = packet->data->read<uint32>(); // 00 00 c3 50
			uint32 clientInst = packet->data->read<uint32>(); 
			uint32 data1 = packet->data->read<uint32>(); // 00 00 c3 50
			uint32 data2 = packet->data->read<uint32>();

			if(clientInst == client->nClientInst)
			{
				/*
				last value 0x00000000 changes faction dunno why but it changes 
				0x00000000 - friendly green
				0x00000001 - red aggressive
				0x00000002 - yellow let say neutral
				0x00000004 - black I don't know what is this
				*/ 
				uint32 fraction = Database.getNpcFraction(data2);
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("GameCharAgentProject", "GameCharInterfaceProject", gameUnknown1, 0, client->nClientInst, 0, 0x00);
				aBuffer.write<uint32>(data);
				aBuffer.write<uint32>(data2);
				aBuffer.write<uint32>(fraction);
				aBuffer.doItAll(client->clientSocket);
			}

			break;
		}
	default:
		{
			Log.Warning("Unknown Packet With Opcode: 0x%08X (%s):\n", packet->opcode, packet->receiver.c_str());
			Log.Warning("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());

			break;
		}
	}
}