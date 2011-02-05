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

void sendPackets::passBlob::CharStats(GameClient* client, uint32 target, uint32* value)
{
	uint32 statsCounter = value[0];
	uint32 size = (4 * 4) + 1 + (statsCounter * 2);
	uint32 pointer = 0;

	if(statsCounter>0)
	{
		PacketBuffer aBuffer(500 + (statsCounter * 8));
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_PassBlob);
		aBuffer.write<uint32>(size);
		aBuffer.write<uint32>(UPDATE_STATS);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(target);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(statsCounter);

		for(int i = 0; i < statsCounter; i++)
		{
			pointer = (i*2)+1;
			aBuffer.write<uint32>(value[pointer]);
			aBuffer.write<uint32>(value[pointer+1]);
		}

		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);
	}
	else
	{
		Log.Error("No values for stats packet\n");
	}
}

void sendPackets::GI_AckAuthentication(GameClient* client, uint32 value)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_AckAuthentication); // AckAuthenticate
	aBuffer.write<uint32>(value); // cDimID
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_AckAuthentication\n");
}

void sendPackets::GI_Pong(Packet* packet, GameClient* client)
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

void sendPackets::GI_ReportDimensionID(GameClient* client, string realmId)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_ReportDimesionID); // ReportDimesionID
	aBuffer.write<string>(realmId); // dimensionID
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_ReportDimensionID\n");
}

void sendPackets::GI_ReportServerID(GameClient* client, uint32 value)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_ReportServerID); // ReportServerID
	aBuffer.write<uint32>(gameUnknown1);
	aBuffer.write<uint32>(value); // serverid? 
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_ReportServerID\n");
}

void sendPackets::other::removeEffect(GlobalTable* GTable, uint32 effectId, uint32 npcId)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, GTable->client->nClientInst, 0, 0x00); // PassBlob
	aBuffer.write<uint32>(0x00000026);
	aBuffer.write<uint32>(0x6b87e316);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(npcId);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(effectId);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(npcId);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(GTable->client->clientSocket);

	Log.Debug("Sent GI_PassBlob - Remove Effect\n");
}