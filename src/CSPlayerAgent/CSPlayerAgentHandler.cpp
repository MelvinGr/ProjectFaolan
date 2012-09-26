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

#include "CSPlayerAgent.h"

void CSPlayerAgent::CSPlayerAgentHandler(Packet* packet, GameClient* client)
{
	Log.Warning("Packet:\n%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());
	//packet->opcode = 0xcdcd;
	switch(packet->opcode)
	{
	case 0x2000: // Authenticate
		{
			client->nClientInst = packet->data->read<uint32>(); // ID64::InstanceType
			client->charInfo.accountID= packet->data->read<uint32>();
			client->charInfo.characterID = client->nClientInst & 0x00ffffff;
			uint32 auth = packet->data->read<uint32>();

			uint8 sender[] = { 0x0d, 0x14, 0x56, 0xd5, 0x6d, 0x10, 0x04 };
			uint8 receiver[] = { 0x0d, 0xc8, 0x60, 0xd5, 0xbb, 0x10, 0x84, 0x80, 0x80, 0x08 };

			PacketBuffer aBuffer(500);
			//aBuffer.writeHeader("CSPlayerAgent", "CSlayerInterface", 1, 0, client->nClientInst, 0, 0x2000);
			aBuffer.writeHeader(sender, sizeof(sender), receiver, sizeof(receiver), 0x2000, true); 
			aBuffer.write<uint32>(auth);
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	default:
		{
			Log.Warning("Unknown Packet With Opcode: 0x%08X\n", packet->opcode);
			Log.Warning("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());

			break;
		}
	}
}