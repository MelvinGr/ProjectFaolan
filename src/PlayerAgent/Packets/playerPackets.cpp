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

#include "../playerAgent.h"
#include "playerPackets.h"

/*******************************************************
    initAuth 
*******************************************************/
void playerPackets::initAuth(Packet* packet, GameClient* client)
{
	client->charInfo.characterID = packet->data->read<uint32>();
	client->charInfo.accountID = client->charInfo.characterID;
	client->nClientInst = client->charInfo.characterID;
	client->nCookie = packet->data->read<uint32>();
	uint32 authStatus = packet->data->read<uint32>();

	uint8 headerData[] = { 0x20, 0x91 };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	
	if(client->nCookie != Database.getAccountCookie(client->nClientInst))
	{
		authStatus = 0xff; //Set auth-error
	}
	//*/
	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x9386ee05);
	aBuffer.write<uint32>(authStatus);
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Send: Auth Init\n");
}


/*******************************************************
    smallCharlist
*******************************************************/
void playerPackets::sendSmallCharList(GameClient* client)
{
	vector<CharacterInfo> characters;
	Database.getCharacters(client->nClientInst, &characters);

	uint32 anzChars = getCharCount(characters);

	uint8 headerData[] = { 0x20 };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xa594a760);
	aBuffer.write<uint32>(((anzChars+1)*1009));
	for(uint32 i = 0; i < characters.size(); i++)
	{
		if(characters[i].level <= 0)
			continue;

		uint32 ClientInst = characters[i].characterID + (characters[i].realmID * 0x01000000);
		aBuffer.write<uint32>(ClientInst);
		aBuffer.write<uint16>(0);
		//aBuffer.write<string>("6f60ebba2cd4881d0393617a01f761b4");
	}
	//*/
	aBuffer.doItAll(client->clientSocket);
	Log.Debug("Send: Small Charlist\n");
}

void playerPackets::sendCharacterList(GameClient* client)
{
	vector<CharacterInfo> characters;
	Database.getCharacters(client->nClientInst, &characters);

	uint8 headerData[] = { 0x20, 0xef };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

	PacketBuffer aBuffer(5000);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x8bd3a00c); // UpdateClientPlayerData
	aBuffer.write<uint32>(client->charInfo.accountID); // PlayerInstance
	uint32 anzChars = getCharCount(characters);

	aBuffer.write<uint32>((anzChars + 1) * 1009); // number of characters
	for(uint32 i = 0; i < characters.size(); i++)
	{
		if(characters[i].level <= 0)
			continue;
		uint32 ClientInst = characters[i].characterID + (characters[i].realmID * 0x01000000);

		aBuffer.write<uint32>(ClientInst); // Charinstance
		aBuffer.write<uint32>(client->charInfo.accountID); // PlayerInstance
		aBuffer.write<uint32>(ClientInst); // Charinstance
		aBuffer.write<string>(characters[i].name); // charName
		aBuffer.write<uint32>(characters[i].realmID); // serverID
		aBuffer.write<uint32>(0x00000000);
		//aBuffer.write<uint32>(characters[i].sex); // Sex
		aBuffer.write<string>(characters[i].lastConnection); //last connection
		aBuffer.write<uint32>(0x00000000); // u1
		aBuffer.write<uint32>(characters[i].map); // playfield
		aBuffer.write<uint32>(characters[i].level); // Charlevel
		aBuffer.write<uint32>(characters[i].Class); // class
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x4f6640b0); // u3
		aBuffer.write<uint32>(0x00000002); // u4
		aBuffer.write<uint32>(characters[i].race); // race
		//aBuffer.write<string>("en");
		aBuffer.write<uint16>(0);
		aBuffer.write<uint32>(0x00000000); // u6
		aBuffer.write<uint32>(0x00000000); // u7
		aBuffer.write<uint16>(0);
		//aBuffer.write<string>("6f60ebba2cd4881d0393617a01f761b4"); // u8
	}
	aBuffer.write<uint32>(0x000003f1);
	aBuffer.write<uint32>(client->charInfo.accountID); // PlayerInstance
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000102); 
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	//aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(Settings.characterSlots); // Number of Char Slots -- default 8
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint16>(0x0000);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.doItAll(client->clientSocket);
	Log.Debug("Send: Full Charlist\n");
}

void playerPackets::sendRealmList(GameClient* client)
{
	uint8 headerData[] = { 0x20, 0xcc };
	uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05  };

	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xe2e6c40f); // SetDimensionList
	aBuffer.write<uint32>(Settings.realms.size()); // Realmcount
	for(uint32 i = 0; i < Settings.realms.size(); i++)
	{
		aBuffer.write<uint32>(Settings.realms[i]->realmID); // realmID
		aBuffer.write<uint32>(Settings.realms[i]->onlineStatus); // onlineStatus - 2 = online, other num = offline
		aBuffer.write<string>(Settings.realms[i]->realmName); // realmname
		aBuffer.write<uint32>(0x00000c02); //u2
		aBuffer.write<uint32>(0x00000000); //u3
		aBuffer.write<uint32>(0x00000000); //u4
		if(Settings.realms[i]->serverActive == 0)
			aBuffer.write<uint32>(1); // server full status 1 = full
		else
			aBuffer.write<uint32>(0); // server full status 1 = full
		aBuffer.write<uint32>(Settings.realms[i]->loadStatus); //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
		aBuffer.write<uint16>(0x0000);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint16>(Settings.realms[i]->realmType); // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
		aBuffer.write<uint32>(0x00000000); //Country: 0 England, 1 France, 2 Spain, 3 German
		aBuffer.write<uint8>(0x00);
	}
		//Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
	aBuffer.doItAll(client->clientSocket);
	Log.Debug("Sent: Realmlist\n");
}

void playerPackets::sendCSServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xe0 };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x14 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	
	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xd4b4d705);
	aBuffer.write<uint32>(ip);
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(accID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(nClientInst);
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Send: CS Server details\n");
}

void playerPackets::sendAgentServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xfa };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x14 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	
	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xe5989d02);
	aBuffer.write<uint32>(ip);
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(accID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(nClientInst);
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Send: Agent Server details\n");
}

void playerPackets::sendWorldServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst)
{
	uint8 headerData[] = { 0x1a, 0x07, 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0x01, 0x20, 0xf9 };
	uint8 sender[] = { 0x0d, 0x16, 0x91, 0x35, 0x1d, 0x10, 0x14 };
	uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
	
	PacketBuffer aBuffer(500);
	aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xd8d9c50d);
	aBuffer.write<uint32>(ip);
	aBuffer.write<uint16>(port);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint16>(0);
	aBuffer.write<uint32>(accID);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(nClientInst);
	aBuffer.write<uint8>(0x62);
	aBuffer.write<uint32>(0x0000c79c);
	aBuffer.write<uint32>(0x00000fa0); //charCreation-Map
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0x00009c50);
	aBuffer.write<uint32>(0x00030bd9);//instance
	Log.Warning("Send Packet:\n%s\n\n", String::arrayToHexString(aBuffer.buffer, aBuffer.bufferLength).c_str());
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Send: World Server details\n");
}

/*####################################################
    Functions
####################################################*/

/*******************************************************
    getCharCounter
*******************************************************/

uint32 playerPackets::getCharCount(vector<CharacterInfo> characters)
{
	uint32 anzChars = characters.size();
	for(uint32 i = 0; i < characters.size(); i++)
	{
		if(characters[i].level == 0)
		{
			anzChars--;
		}
	}
	if(anzChars >= 0)
	{
		return anzChars;
	}
	else
	{
		Log.Error("ERROR: Can't get the correct count of the Characters!\n");
		return 0;
	}
}