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

#include "PlayerAgent.h"

void PlayerAgent::sendCharacterList(GameClient* client)
{
	vector<CharacterInfo> characters;
	Database.getCharacters(client->nClientInst, &characters);

	PacketBuffer aBuffer(5000);
	aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xc414c5ef); // UpdateClientPlayerData
	aBuffer.write<uint32>(client->charInfo.accountID); // PlayerInstance
	uint32 anz_chars = characters.size();
	for(uint32 i = 0; i < characters.size(); i++)
	{
		if(characters[i].level == 0)
		{
			anz_chars--;
		}
	}
	aBuffer.write<uint32>((anz_chars + 1) * 1009); // number of characters

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
		aBuffer.write<uint32>(characters[i].sex); // Sex
		aBuffer.write<string>(characters[i].lastConnection); //last connection
		aBuffer.write<uint32>(0x00000000); // u1
		aBuffer.write<uint32>(characters[i].map); // playfield
		aBuffer.write<uint32>(characters[i].level); // Charlevel
		aBuffer.write<uint32>(characters[i].Class); // class
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x00000000); // u2
		aBuffer.write<uint32>(0x4bbafa33); // u3
		aBuffer.write<uint32>(0x00000002); // u4
		aBuffer.write<uint32>(characters[i].race); // race
		aBuffer.write<string>("en");
		aBuffer.write<uint32>(0x00000000); // u6
		aBuffer.write<uint32>(0x00000000); // u7
		aBuffer.write<string>("6f60ebba2cd4881d0393617a01f761b4"); // u8
	}
	aBuffer.write<uint32>(Settings.characterSlots); // Number of Char Slots -- default 8
	aBuffer.write<uint32>(0x000007e2);
	aBuffer.write<uint32>(0x00000002); // u9
	aBuffer.write<uint32>(0x0000000a); // u10
	aBuffer.write<uint32>(0x0000004a); // u11
	aBuffer.doItAll(client->clientSocket);
}

void PlayerAgent::sendRealmList(GameClient* client)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xf899b14c); // SetDimensionList
	aBuffer.write<uint32>(Settings.realms.size()); // Realmcount
	for(uint32 i = 0; i < Settings.realms.size(); i++)
	{
		aBuffer.write<uint32>(Settings.realms[i]->realmID); // realmID
		aBuffer.write<uint32>(Settings.realms[i]->onlineStatus); // onlineStatus - 2 = online, other num = offline
		aBuffer.write<uint32>(0x0007a120); //u1
		aBuffer.write<string>(Settings.realms[i]->realmName); // realmname
		aBuffer.write<uint32>(0x00000002); //u2
		aBuffer.write<uint32>(0x00000000); //u3
		aBuffer.write<uint32>(0x00000000); //u4
		aBuffer.write<uint32>(0x00000000); // server full status 1 = full
		aBuffer.write<uint32>(Settings.realms[i]->loadStatus); //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
		aBuffer.write<uint32>(0x00000000); //u7
		aBuffer.write<uint32>(0x00000000); //u8
		aBuffer.write<uint16>(Settings.realms[i]->realmType); // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
		aBuffer.write<uint32>(0x00000000); //u9
	}
	aBuffer.doItAll(client->clientSocket);
}

void PlayerAgent::PlayerAgentHandler(Packet* packet, GameClient* client)
{
	Log.Debug("Receive opcode: 0x%08x\n", packet->opcode);
	switch(packet->opcode)
	{
	case 0x9cb2cb03: // Authenticate
		{
			client->nClientInst = packet->data->read<uint32>();
			client->nCookie = packet->data->read<uint32>();
			client->charInfo.characterID = client->nCookie;
			client->charInfo.accountID = client->nClientInst;

			Database.deleteEmptyChar(client->charInfo.accountID);
			Database.insertEmptyChar(client);

			/*if(true)  //isCharacterLoggedIn(client->nClientInst))
			{
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("ServerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd4063ca0); // Playerloggedin
			aBuffer.write<uint32>(0x0000000b);
			aBuffer.doItAll(client->clientSocket);

			break;
			}*/
			printf("Client-Cookie-> %i\n", client->nCookie);

			if(client->nCookie == Database.getAccountCookie(client->nClientInst))
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5dc18991); // AuthenticateAck
				aBuffer.write<uint32>(0x01); // nStatus
				aBuffer.doItAll(client->clientSocket);
			}
			else
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5dc18991); // AuthenticateAck
				aBuffer.write<uint32>(0xff); // nStatus
				aBuffer.doItAll(client->clientSocket);
			}

			break;
		}

	case 0xBE735486: // CreateCharacter
		{
			Log.Error("Get second char creation pack\n");
			uint32 i_nDimID = packet->data->read<uint32>();

			uint32 unk1 = packet->data->read<uint32>(); // dont know if these are correct values
			uint32 unk2 = packet->data->read<uint32>();
			uint8 unk3 = packet->data->read<uint8>();

			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == i_nDimID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}

			if(realm == 0) // send error opcode, whatever
				break;

			//client->charInfo.accountID = client->nClientInst;
			//client->nClientInst += (i_nDimID * 0x01000000);
			client->nClientInst = client->charInfo.characterID + (i_nDimID * 0x01000000);

			//client->charInfo.characterID = (Database.getNewCharacterID() + 1) * 1009;
			if(client->charInfo.characterID == -1) // send error opcode, whatever
				break;

			//Database.insertEmptyChar(client);
			//client->nClientInst = Network::MakeClientInst(realm->realmID, client->charInfo.characterID);

			PacketBuffer aBuffer(500);
			/*
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->csPlayerAgentIPAddress.c_str()))); // CSPlayer
			aBuffer.write<uint16>(realm->csPlayerAgentPort); // CSport
			aBuffer.write<uint32>(client->charInfo.characterID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			//*/
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x23a632fa);
			aBuffer.write<uint32>(htonl(inet_addr(realm->agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			aBuffer.write<uint16>(realm->agentServerPort);
			//aBuffer.write<uint32>(client->charInfo.characterID);
			aBuffer.write<uint32>(client->charInfo.accountID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->csPlayerAgentIPAddress.c_str()))); // CSPlayer
			aBuffer.write<uint16>(realm->csPlayerAgentPort); // CSport
			aBuffer.write<uint32>(client->charInfo.accountID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(client->charInfo.accountID); //
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst); //
			aBuffer.write<uint8>(0x62); //
			aBuffer.write<uint32>(0x0000c79c); //
			aBuffer.write<uint32>(client->charInfo.map); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.write<uint32>(0x00009c50); //
			aBuffer.write<uint32>(0x00036bc1); // cookie
			aBuffer.doItAll(client->clientSocket);
			/*
			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(client->charInfo.characterID); //
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst); //
			aBuffer.write<uint8>(0x62); //
			aBuffer.write<uint32>(0x0000c79c); //
			aBuffer.write<uint32>(0x00000fa0); //StartMAP Stygian Galley
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.write<uint32>(0x00009c50); //
			aBuffer.write<uint32>(0x00036bc1); // cookie
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xf4116428);
			aBuffer.write<uint32>(0x000007e2); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.doItAll(client->clientSocket);
			//*/
			break;
		}

	case 0xEF616EB6: // LoginCharacter:
		{
			client->nClientInst = packet->data->read<uint32>();
			uint32 unk = packet->data->read<uint32>();
			string cClientLanguage = packet->data->read<string>();
			client->charInfo.characterID = client->nClientInst & 0x00ffffff;

			uint32 unk1 = packet->data->read<uint32>(); // dont know if these are correct values
			uint16 unk2 = packet->data->read<uint16>();
			uint8 unk3 = packet->data->read<uint8>();

			Database.deleteCharacter(client->nCookie);

			if(!Database.getCharacterInfo(client->charInfo.characterID , &client->charInfo))
			{
				// send error opcode, whatever
				Log.Error("INTERNAL ERROR\n");
				break;
			}
			//*/

			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == client->charInfo.realmID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}

			if(realm == 0) // send error opcode, whatever
			{
				Log.Error("Realm-Error!!!\n");
				break;
			}

			//client->nClientInst = client->charInfo.accountID + (client->charInfo.realmID * 0x01000000);
			client->nClientInst = client->charInfo.characterID + (client->charInfo.realmID * 0x01000000);

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->csPlayerAgentIPAddress.c_str()))); // CSPlayer
			aBuffer.write<uint16>(realm->csPlayerAgentPort); // CSport
			aBuffer.write<uint32>(client->charInfo.accountID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x23a632fa);
			aBuffer.write<uint32>(htonl(inet_addr(realm->agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			aBuffer.write<uint16>(realm->agentServerPort);
			aBuffer.write<uint32>(client->charInfo.accountID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(client->charInfo.accountID); //
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst); //
			aBuffer.write<uint8>(0x62); //
			aBuffer.write<uint32>(0x0000c79c); //
			aBuffer.write<uint32>(client->charInfo.map); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.write<uint32>(0x00009c50); //
			aBuffer.write<uint32>(0x00036bc1); // cookie
			aBuffer.doItAll(client->clientSocket);
			
			break;
		}
	case 0xCA2C4E5E:
		{
			client->charInfo.realmID = packet->data->read<uint32>();
			/*
			 000006E8  00 00 00 42 4b 7d b2 74  00 0b 50 6c 61 79 65 72 ...BK}.t ..Player
    000006F8  41 67 65 6e 74 00 00 00  06 00 00 00 00 00 0f 50 Agent... .......P
    00000708  6c 61 79 65 72 49 6e 74  65 72 66 61 63 65 59 47 layerInt erfaceYG
    00000718  c8 29 00 00 00 00 23 36  05 b9 00 00 c3 50 01 0a .)....#6 .....P..
    00000728  82 7e 00 00 00 00
			*/
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x233605b9); // CSServerConnectReady
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->charInfo.characterID);
			aBuffer.write<uint32>(0);
			aBuffer.doItAll(client->clientSocket);

			break;
		}
	case 0x3c7c926c:
		{
			
			Log.Debug("Receive Create Char\n");
			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == client->charInfo.realmID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}

			if(realm == 0) // send error opcode, whatever
			{
				Log.Error("Realm-Error!!!\n");
				break;
			}

			PacketBuffer aBuffer(500);
			
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->csPlayerAgentIPAddress.c_str()))); // CSPlayer
			aBuffer.write<uint16>(realm->csPlayerAgentPort); // CSport
			aBuffer.write<uint32>(client->charInfo.characterID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x23a632fa);
			aBuffer.write<uint32>(htonl(inet_addr(realm->agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			aBuffer.write<uint16>(realm->agentServerPort);
			aBuffer.write<uint32>(client->charInfo.characterID);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(htonl(inet_addr(realm->worldServerIPAddress.c_str()))); // i_nGameAddr
			aBuffer.write<uint16>(realm->worldServerPort); // game port
			aBuffer.write<uint32>(client->nClientInst);//client->charInfo.characterID); //
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->charInfo.characterID);//client->nClientInst); //
			aBuffer.write<uint8>(0x62); //
			aBuffer.write<uint32>(0x0000c79c); //
			aBuffer.write<uint32>(client->charInfo.map); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000000); //
			aBuffer.write<uint32>(0x00000001); //
			aBuffer.write<uint32>(0x00009c50); //
			aBuffer.write<uint32>(0x00036bc1); // cookie
			aBuffer.doItAll(client->clientSocket);

			aBuffer = PacketBuffer(500);
			//*/
			
			aBuffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xf4116428);
			aBuffer.write<uint32>(0x000003f1); //
			aBuffer.doItAll(client->clientSocket);
			//*/
			break;
		}

	case 0x03: // RenameCharacter
		{
			uint64 nCharInstance = packet->data->read<uint32>();
			string cNewName = packet->data->read<string>();

			//

			break;
		}

	case 0xc3c5c31d: // RequestDeleteCharacter
		{
			uint32 nCharacterInstance = packet->data->read<uint32>();
			Log.Notice("REQUEST DELETE CHAR\n\n");
			Database.deleteCharacter(nCharacterInstance / 1009 - 1);
			sendCharacterList(client);

			break;
		}

	case 0x9847aed6: // VerifyLanguageSetting
		{
			// Inmy client lang is 04 PL 
			uint32 nClientLanguage = packet->data->read<uint32>();
			if(nClientLanguage==0) // if it's english client with enlish realm
			{
				//sendCharacterList(client);
			}
			else
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0xd4c59816); // Set the realm language to same as client ?
				aBuffer.write<uint32>(0x00000000); // server language 0 is english 
				aBuffer.doItAll(client->clientSocket);
			}

			break;
		}

	case 0x06: // GetDimensionList
		{
			sendRealmList(client);

			break;
		}

	case 0x07: // RequestSuggestNickname
		{
			uint32 i_nRace = packet->data->read<uint32>();
			uint32 i_nSex = packet->data->read<uint32>();

			//

			break;
		}

	case 0x6a546d41: // GetStartupData
		{
			
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x0c09ca25); // ???
			aBuffer.write<uint32>(0x000003f1);
			aBuffer.doItAll(client->clientSocket);
			
			//Then wait

			//sendCharacterList(client);
			//sendRealmList(client);
			/*
			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x4f91a58c); // PlayerSetupComplete
			aBuffer.write<uint32>(0x01); // authstatus
			aBuffer.doItAll(client->clientSocket);
			//*/

			break;
		}
	case 0xdfd8518e:
		{
			sendCharacterList(client);
			sendRealmList(client);

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, client->nClientInst, 0, 0x4f91a58c); // PlayerSetupComplete
			aBuffer.write<uint32>(0x01); // authstatus
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