/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009 - 2012 The Project Faolan Team

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

#include "../UniverseAgent.h"
#include "universePackets.h"

/*******************************************************
    initAuth 
*******************************************************/
void universePackets::initAuth(Packet* packet, GameClient* client)
{
	string cUserName = packet->data->read<string>();
	
	PacketBuffer aBuffer(500);
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xdc, 0xc8, 0x49 };

	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), 0, 0, 0x2000, true);
	aBuffer.write<string>(client->authChallenge);
	aBuffer.doItAll(client->clientSocket);
}

/*******************************************************
    setRegionState 
*******************************************************/
void universePackets::setRegionState(Packet* packet, GameClient* client)
{
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	uint8 real[] = { 0x01, 0x01, 0x01, 0x01, 0x3f, 0x80, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x01, 0x00 };

	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), 0, 0, 0x2005, true);
	aBuffer.writeArray(real, sizeof(real));
	aBuffer.doItAll(client->clientSocket);
}

/*******************************************************
    AckAuthenticate 
*******************************************************/
void universePackets::AckAuthenticate(Packet*packet, GameClient* client, uint32 authStatus, uint64 playerID, string cTerritoryManagerAddr, uint32 cookie, uint32 eReason, uint16 errorCode)
{
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), 0, 0, 0x2001, true);
	aBuffer.write<uint32>(authStatus);
	aBuffer.write<uint64>(playerID);
	if(errorCode == 0)
	{
		aBuffer.write<string>(cTerritoryManagerAddr);
	}
	aBuffer.write<uint32>(cookie);
	aBuffer.write<uint32>(eReason);
	if(errorCode > 0)
	{
		aBuffer.write<uint16>(errorCode);
	}
	aBuffer.doItAll(client->clientSocket);
}