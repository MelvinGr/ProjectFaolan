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

#include "PlayerConnection.h"

#include "../Common/Configuration.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

void PlayerConnection::WritePlayerHeader(PacketBuffer &packetBuffer, uint8 sender[], uint8 sl, uint8 receiver[], uint8 rl, 
										 uint8 headerData[], uint8 hl, uint32 opcode, bool minOpcode)
{
	packetBuffer.write<uint32>(0); // Write empty length
	packetBuffer.write<uint32>(0); // write empty crc32
	packetBuffer.write<uint32>(sl + rl + hl + sizeof(uint8) * 4 + (minOpcode ? sizeof(uint16) : sizeof(uint32)));
	packetBuffer.write<uint8>(0x0a); // sender
	packetBuffer.write<uint8>(sl); // sender length
	packetBuffer.write(sender, sl); // sender data
	packetBuffer.write<uint8>(0x12); // receiver
	packetBuffer.write<uint8>(rl); // receiver length
	packetBuffer.write(receiver, rl); // receiver data

	if(hl > 0)
		packetBuffer.write(headerData, hl); // header data

	if(minOpcode)
		packetBuffer.write<uint16>(opcode); // opcode
	else
		packetBuffer.write<uint32>(opcode); // opcode
}

/*******************************************************
initAuth 
*******************************************************/
void PlayerConnection::InitAuth(Packet &packet)
{
	gameClient.accountInfo.accountID = packet.data->read<uint32>();
	gameClient.accountInfo.cookie = packet.data->read<uint32>();

	//gameClient.charInfo.accountID = gameClient.charInfo.characterID;
	//gameClient.nClientInst = gameClient.charInfo.characterID;

	uint32 authStatus = packet.data->read<uint32>();

	uint8 headerData[] = { 0x20, 0x91 };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	if(gameClient.accountInfo.cookie != MysqlFunctions::GetAccountCookie(gameClient.accountInfo.accountID))
		authStatus = 0xff; //Set auth-error

	PacketBuffer aBuffer(500);
	//aBuffer.setHeader(0x1c, 0x0a070d84, 0x04f28210, 0x03120b0d, 0x3857157d, headerData, 0x9386ee05);
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x9386ee05);
	aBuffer.write<uint32>(authStatus);
	SendPacket(aBuffer);

	printf("Send: Auth Init\n");
}

/*******************************************************
smallCharlist
*******************************************************/
void PlayerConnection::SendSmallCharList()
{
	vector<CharacterInfo> characters;
	MysqlFunctions::GetCharacters(gameClient.accountInfo.accountID, characters);

	uint32 anzChars = characters.size();// getCharCount(characters);

	uint8 headerData[] = { 0x20 };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	//aBuffer.setHeader(0x1b, 0x0a070d84, 0x04f28210, 0x03120b0d, 0x3857157d, headerData, 0xa594a760);
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xa594a760);
	aBuffer.write<uint32>((anzChars + 1) * 1009);

	foreach(const CharacterInfo &characterInfo, characters)
	{
		if(characterInfo.level <= 0)
			continue;

		uint32 ClientInst = characterInfo.characterID + (characterInfo.realmID * 0x01000000);
		aBuffer.write<uint32>(ClientInst);
		aBuffer.write<string>("6f60ebba2cd4881d0393617a01f761b4");
	}

	SendPacket(aBuffer);
	printf("Send: Small Charlist\n");
}

void PlayerConnection::SendCharacterList()
{
	vector<CharacterInfo> characters;
	MysqlFunctions::GetCharacters(gameClient.accountInfo.accountID, characters);

	uint8 headerData[] = { 0x20, 0xef };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	uint32 anzChars = characters.size();// getCharCount(characters);

	PacketBuffer aBuffer(5000);
	//aBuffer.setHeader(0x1c, 0x0a070d84, 0x04f28210, 0x03120b0d, 0x3857157d, headerData, 0x8bd3a00c); // UpdateClientPlayerData
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x8bd3a00c); // UpdateClientPlayerData
	aBuffer.write<uint32>(gameClient.accountInfo.accountID); // PlayerInstance
	aBuffer.write<uint32>((anzChars + 1) * 1009); // number of characters

	foreach(const CharacterInfo &characterInfo, characters)
	{
		if(characterInfo.level <= 0)
			continue;

		uint32 ClientInst = characterInfo.characterID + (characterInfo.realmID * 0x01000000);

		aBuffer.write<uint32>(ClientInst); // Charinstance
		aBuffer.write<uint32>(gameClient.accountInfo.accountID); // PlayerInstance
		aBuffer.write<uint32>(ClientInst); // Charinstance
		aBuffer.write<string>(characterInfo.name); // charName
		aBuffer.write<uint32>(characterInfo.realmID); // serverID
		aBuffer.write<uint32>(0x00000000);
		//aBuffer.write<uint32>(characterInfo.sex); // Sex
		aBuffer.write<string>(characterInfo.lastConnection); //last connection
		aBuffer.write<uint32>(0x00000000); // u1
		aBuffer.write<uint32>(characterInfo.map); // playfield
		aBuffer.write<uint32>(characterInfo.level); // Charlevel
		aBuffer.write<uint32>(characterInfo.Class); // class
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x4f6640b0); // u3
		aBuffer.write<uint32>(0x00000002); // u4
		aBuffer.write<uint32>(characterInfo.race); // race
		//aBuffer.write<string>("en");
		aBuffer.write<uint16>(0);
		aBuffer.write<uint32>(0x00000000); // u6
		aBuffer.write<uint32>(0x00000000); // u7
		aBuffer.write<string>("6f60ebba2cd4881d0393617a01f761b4"); // u8
	}

	aBuffer.write<uint32>(0x000003f1);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID); // PlayerInstance
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000102); 
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	//aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(Config.GetValue<uint32>("characterSlots")); // Number of Char Slots -- default 8
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000001);
	SendPacket(aBuffer);

	printf("Send: Full Charlist\n");
}

void PlayerConnection::SendRealmList()
{
	vector<RealmInfo> realms;
	MysqlFunctions::GetRealms(realms);

	uint8 headerData[] = { 0x20, 0xcc };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xec, 0xeb, 0x80, 0xde, 0x03 }; // 0xeb, 0x8e, 0x95, 0xbf, 0x05

	PacketBuffer aBuffer(500);
	//aBuffer.setHeader(0x1c, 0x0a070d84, 0x04f28210, 0x03120b0d, 0x3857157d, headerData, 0xe2e6c40f); // SetDimensionList
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xe2e6c40f); // SetDimensionList
	aBuffer.write<uint32>(realms.size()); // Realmcount

	// Dont know if the layout is correct, name, status, type are displayed correct. so...
	foreach(const RealmInfo &realmInfo, realms)
	{
		aBuffer.write<uint32>(realmInfo.realmID); // realmID
		aBuffer.write<uint32>(realmInfo.worldServerActive); // onlineStatus - 2 = online, other num = offline
		aBuffer.write<string>(realmInfo.realmName); // realmname 
		aBuffer.write<uint32>(0x00000c02); //u2
		aBuffer.write<uint32>(0x00000000); //u3
		aBuffer.write<uint32>(0x00000000); //u4
		aBuffer.write<uint32>(0x00000000); // u5
		aBuffer.write<uint32>(realmInfo.loadStatus); //load status ???? -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
		aBuffer.write<uint8>(0x00000000);
		aBuffer.write<uint16>(0x00000000);
		aBuffer.write<uint16>(realmInfo.realmType); // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
		aBuffer.write<uint8>(0x00000000);
		aBuffer.write<uint32>(0x00000000);
	}

	SendPacket(aBuffer);

	printf("Send: Realmlist\n");
}

void PlayerConnection::SendCSServerAddress(const string &ip, uint32 port)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xe0 };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x12 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xd4b4d705);
	aBuffer.write<uint32>(htonl(inet_addr(ip.c_str())));
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	SendPacket(aBuffer);

	printf("Send: CS Server details\n");
}

void PlayerConnection::SendAgentServerAddress(const string &ip, uint32 port)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xfa };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x12 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xe5989d02);
	aBuffer.write<uint32>(htonl(inet_addr(ip.c_str())));
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	SendPacket(aBuffer);

	printf("Send: Agent Server details\n");
}

void PlayerConnection::SendWorldServerAddress(const string &ip, uint32 port)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xf9 };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x12 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xd8d9c50d);
	aBuffer.write<uint32>(htonl(inet_addr(ip.c_str())));
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint16>(0);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(gameClient.accountInfo.accountID);
	aBuffer.write<uint8>(0x62);
	aBuffer.write<uint32>(0x0000c89c);
	aBuffer.write<uint32>(0x00000fa0); //charCreation-Map
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x00000003);
	aBuffer.write<uint32>(0x00009c50);
	aBuffer.write<uint32>(0x0011532f);
	SendPacket(aBuffer);

	printf("Send: World Server details\n");
}