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

#include "UniverseConnection.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

#include "../Common/Configuration.h"

//#include <initializer_list>

using namespace std;

void UniverseConnection::WriteUniverseHeader(PacketBuffer &packetBuffer, uint8 sender[], uint8 sl, uint8 receiver[], uint8 rl, uint16 opcode)
{
	packetBuffer.write<uint32>(0); // Write empty length
	packetBuffer.write<uint32>(0); // write empty crc32
	packetBuffer.write<uint32>(sl + rl + sizeof(uint8) * 4 + sizeof(uint16)); // header length
	packetBuffer.write<uint8>(0x0a); // sender
	packetBuffer.write<uint8>(sl); // sender length
	packetBuffer.write(sender, sl); // sender data
	packetBuffer.write<uint8>(0x12); // receiver
	packetBuffer.write<uint8>(rl); // receiver length
	packetBuffer.write(receiver, rl); // receiver data
	packetBuffer.write<uint16>(opcode); // opcode
}

/*******************************************************
initAuth 
*******************************************************/
void UniverseConnection::InitAuth(Packet &packet)
{
	string cUserName = packet.data->read<string>();
	uint32 nGameID = packet.data->read<uint32>();

	PacketBuffer aBuffer(500);
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xe4, 0xa5, 0x41 };

	WriteUniverseHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), 0x2000);
	aBuffer.write<string>(gameClient.authChallenge);	
	SendPacket(aBuffer);
}

/*******************************************************
setRegionState 
*******************************************************/
void UniverseConnection::SetRegionState(Packet &packet)
{
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	uint8 real[] = { 0x01, 0x01, 0x01, 0x01, 0x3f, 0x80, 0x00, 0x00, 0x3f, 0x80, 0x00, 0x00, 0x01, 0x01, 0x01 };

	PacketBuffer aBuffer(500);
	WriteUniverseHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), 0x2005);
	aBuffer.write(real, sizeof(real));
	SendPacket(aBuffer);
}

/*******************************************************
AckAuthenticate 
*******************************************************/
void UniverseConnection::AckAuthenticate(Packet &packet, uint32 authStatus, uint32 eReason, uint16 errorCode)
{
	uint8 sender[] = { 0x0d, 0xa0, 0xdb, 0x4d, 0x60, 0x10, 0x01 };
	uint8 receiver[] = { 0x0d, 0x54, 0x40, 0x38, 0x0c, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	uint64 accID = 0x0000271200000000 + gameClient.accountInfo.accountID;
	//accID = 0x0000271257e5476b;

	gameClient.accountInfo.cookie = rand();
	MysqlFunctions::SetAccountCookie(gameClient.accountInfo.accountID, gameClient.accountInfo.cookie);

	printf("Accountcookie: 0x%08x\nClientInst: 0x%08x\n", gameClient.accountInfo.cookie, gameClient.accountInfo.accountID);

	PacketBuffer aBuffer(500);
	WriteUniverseHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), 0x2001);
	aBuffer.write<uint32>(authStatus);
	aBuffer.write<uint64>(accID);

	if(errorCode == 0)
	{
		string cTerritoryManagerAddr = Config.GetValue<string>("playerAgentIPAddress") + ":" + Config.GetValue<string>("playerAgentPort");
		aBuffer.write<string>(cTerritoryManagerAddr);
	}

	aBuffer.write<uint32>(gameClient.accountInfo.cookie);
	aBuffer.write<uint32>(eReason);

	if(errorCode > 0)
		aBuffer.write<uint16>(errorCode);

	SendPacket(aBuffer);
}