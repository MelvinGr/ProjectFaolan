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

void sendPackets::GI_PingPong(Packet* packet, GameClient* client)
{
	uint32 cSendTime = packet->data->read<uint32>(); // ACE_Time_Value
	uint32 lastData = packet->data->read<uint32>();

	time_t nServerTime;
	time(&nServerTime);

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_Pong); 
	aBuffer.write<uint32>(cSendTime);
	aBuffer.write<uint32>(nServerTime);
	aBuffer.write<uint32>(lastData);
	aBuffer.write<uint32>(nServerTime);
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_Pong\n");
}

void sendPackets::GCI_PingPong(Packet* packet, GameClient* client)
{
	uint32 i_nSequence = packet->data->read<uint32>();
	//uint32 lastdata = packet->data->read<uint32>();

	time_t nServerTime;
	time(&nServerTime);

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GCI_Pong); // Pong
	aBuffer.write<uint32>(i_nSequence); // i_nSequence
	//aBuffer.write<uint32>(lastdata); // lastdata
	aBuffer.write<uint32>(0x00000026); // lastdata
	aBuffer.write<uint32>(nServerTime); // nServerTime
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GCI_Pong\n");
}