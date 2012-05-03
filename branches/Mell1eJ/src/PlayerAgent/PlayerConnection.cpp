/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful, 
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#include "PlayerConnection.h"

PlayerConnection::PlayerConnection(boost::asio::io_service& IOService, BufferPool* hp) : Connection(IOService, hp)
{
	//
}

PlayerConnection::~PlayerConnection()
{
	//
}

void PlayerConnection::start()
{
	printf("New connection accepted\n");

	AsyncRead();
}

void PlayerConnection::SendCharacterList()
{
	vector<CharacterInfo> characters;
	if(!MySQLFunctions::GetCharacters(gameClient.nClientInst, &characters))
	{
		// send error to client?
		printf("Error while fetching characters!");
	}

	PacketBuffer *buffer = m_bufferPool->allocateBuffer(2000);
	buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xc414c5ef); // UpdateClientPlayerData
	buffer->write<uint32>(gameClient.nClientInst); // PlayerInstance
	buffer->write<uint32>((characters.size() + 1) * 1009); // number of characters

	foreach(CharacterInfo character, characters)
	{
		if(character.level <= 0)
			continue;

		uint32 ClientInst = character.characterID + (character.realmID * 0x01000000);

		buffer->write<uint32>(ClientInst); // Charinstance
		buffer->write<uint32>(gameClient.nClientInst); // PlayerInstance
		buffer->write<uint32>(ClientInst); // Charinstance
		buffer->write<string>(character.name); // charName
		buffer->write<uint32>(character.realmID); // serverID
		buffer->write<uint32>(character.sex); // Sex
		buffer->write<string>(character.lastConnection); //last connection
		buffer->write<uint32>(0x00000000); // u1
		buffer->write<uint32>(character.map); // playfield
		buffer->write<uint32>(character.level); // Charlevel
		buffer->write<uint32>(character.Class); // class
		buffer->write<uint32>(0x00000000); // u2
		buffer->write<uint32>(0x00000000); // u2
		buffer->write<uint32>(0x4bbafa33); // u3
		buffer->write<uint32>(0x00000002); // u4
		buffer->write<uint32>(character.race); // race
		buffer->write<string>("en");
		buffer->write<uint32>(0x00000000); // u6
		buffer->write<uint32>(0x00000000); // u7
		buffer->write<string>("6f60ebba2cd4881d0393617a01f761b4"); // u8
	}

	buffer->write<uint32>(Config.characterSlots); // Number of Char Slots -- default 8
	buffer->write<uint32>(0x000007e2);
	buffer->write<uint32>(0x00000002); // u9
	buffer->write<uint32>(0x0000000a); // u10
	buffer->write<uint32>(0x0000004a); // u11
	SendPacket(buffer);

	m_bufferPool->disposeBuffer(buffer);
}

void PlayerConnection::SendRealmList()
{	
	vector<RealmInfo> realms;
	if(!MySQLFunctions::GetRealms(&realms))
	{
		// send error to client?
		printf("Error while fetching realms!");
	}

	PacketBuffer *buffer = m_bufferPool->allocateBuffer(1000);
	buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xf899b14c); // SetDimensionList
	buffer->write<uint32>(realms.size()); // Realmcount

	foreach(RealmInfo realm, realms)
	{
		buffer->write<uint32>(realm.realmID); // realmID
		buffer->write<uint32>(realm.onlineStatus); // onlineStatus - 2 = online, other num = offline
		buffer->write<uint32>(0x0007a120); //u1
		buffer->write<string>(realm.realmName); // realmname
		buffer->write<uint32>(0x00000002); //u2
		buffer->write<uint32>(0x00000000); //u3
		buffer->write<uint32>(0x00000000); //u4
		buffer->write<uint32>(0x00000000); // server full status 1 = full
		buffer->write<uint32>(realm.loadStatus); //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
		buffer->write<uint32>(0x00000000); //u7
		buffer->write<uint32>(0x00000000); //u8
		buffer->write<uint16>(realm.realmType); // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
		buffer->write<uint32>(0x00000000); //u9
	}

	SendPacket(buffer);
	m_bufferPool->disposeBuffer(buffer);
}

void PlayerConnection::handlePacket(PacketBuffer *packetBuffer, Packet* packet)
{
	PacketBuffer *buffer = m_bufferPool->allocateBuffer(2000);
	switch(packet->opcode)
	{
	case 0x9cb2cb03: // Authenticate
		{
			gameClient.nClientInst = packet->buffer->read<uint32>();
			gameClient.nCookie = packet->buffer->read<uint32>();
			characterInfo.characterID = gameClient.nCookie;
			characterInfo.accountID = gameClient.nClientInst;
			
			if(true)//gameClient.nCookie == MySQLFunctions::GetAccountCookie(gameClient.nClientInst))
			{
				buffer->reset();
				buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5dc18991); // AuthenticateAck
				buffer->write<uint32>(0x01); // nStatus
				SendPacket(buffer);
			}
			else
			{
				buffer->reset();
				buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5dc18991); // AuthenticateAck
				buffer->write<uint32>(0xff); // nStatus
				SendPacket(buffer);
			}

			break;
		}

	case 0xBE735486: // CreateCharacter
		{
			printf("Get second char creation pack\n");
			uint32 i_nDimID = packet->buffer->read<uint32>();

			uint32 unk1 = packet->buffer->read<uint32>(); // dont know if these are correct values
			uint32 unk2 = packet->buffer->read<uint32>();
			uint8 unk3 = packet->buffer->read<uint8>();

			RealmInfo realm;
			if(!MySQLFunctions::GetRealm(characterInfo.realmID, &realm))
			{
				// send error opcode, whatever
				printf("INTERNAL ERROR\n");
				break;
			}

			gameClient.nClientInst = characterInfo.characterID + (i_nDimID * 0x01000000);

			if(characterInfo.characterID == -1) // send error opcode, whatever
				break;

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer->write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer->write<uint16>(realm.agentServerPort);
			buffer->write<uint32>(characterInfo.accountID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer->write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer->write<uint32>(characterInfo.accountID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(characterInfo.accountID); //
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst); //
			buffer->write<uint8>(0x62); //
			buffer->write<uint32>(0x0000c79c); //
			buffer->write<uint32>(characterInfo.map); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000001); //
			buffer->write<uint32>(0x00009c50); //
			buffer->write<uint32>(0x00036bc1); // cookie
			SendPacket(buffer);

			break;
		}

	case 0xEF616EB6: // LoginCharacter:
		{
			gameClient.nClientInst = packet->buffer->read<uint32>();
			uint32 unk = packet->buffer->read<uint32>();
			string cClientLanguage = packet->buffer->read<string>();
			characterInfo.characterID = gameClient.nClientInst & 0x00ffffff;

			uint32 unk1 = packet->buffer->read<uint32>(); // dont know if these are correct values
			uint16 unk2 = packet->buffer->read<uint16>();
			uint8 unk3 = packet->buffer->read<uint8>();
			
			if(!MySQLFunctions::GetCharacter(characterInfo.characterID , &characterInfo))
			{
				// send error opcode, whatever
				printf("INTERNAL ERROR\n");
				break;
			}

			RealmInfo realm;
			if(!MySQLFunctions::GetRealm(characterInfo.realmID, &realm))
			{
				// send error opcode, whatever
				printf("INTERNAL ERROR\n");
				break;
			}

			gameClient.nClientInst = characterInfo.characterID + (characterInfo.realmID * 0x01000000);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer->write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer->write<uint32>(characterInfo.accountID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer->write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer->write<uint16>(realm.agentServerPort);
			buffer->write<uint32>(characterInfo.accountID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(characterInfo.accountID); //
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst); //
			buffer->write<uint8>(0x62); //
			buffer->write<uint32>(0x0000c79c); //
			buffer->write<uint32>(characterInfo.map); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000001); //
			buffer->write<uint32>(0x00009c50); //
			buffer->write<uint32>(0x00036bc1); // cookie
			SendPacket(buffer);

			break;
		}
	case 0xCA2C4E5E:
		{
			characterInfo.realmID = packet->buffer->read<uint32>();

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x233605b9); // CSServerConnectReady
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(characterInfo.characterID);
			buffer->write<uint32>(0);
			SendPacket(buffer);

			break;
		}
	case 0x3c7c926c:
		{
			printf("Receive Create Char\n");

			RealmInfo realm;
			if(!MySQLFunctions::GetRealm(characterInfo.realmID, &realm))
			{
				// send error opcode, whatever
				printf("INTERNAL ERROR\n");
				break;
			}

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer->write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer->write<uint32>(characterInfo.characterID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer->write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer->write<uint16>(realm.agentServerPort);
			buffer->write<uint32>(characterInfo.characterID);
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(gameClient.nClientInst);
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer->write<uint16>(realm.worldServerPort); // game port
			buffer->write<uint32>(gameClient.nClientInst);//characterInfo.characterID); //
			buffer->write<uint32>(0x0000c350);
			buffer->write<uint32>(characterInfo.characterID);//gameClient.nClientInst); //
			buffer->write<uint8>(0x62); //
			buffer->write<uint32>(0x0000c79c); //
			buffer->write<uint32>(characterInfo.map); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000000); //
			buffer->write<uint32>(0x00000001); //
			buffer->write<uint32>(0x00009c50); //
			buffer->write<uint32>(0x00036bc1); // cookie
			SendPacket(buffer);

			buffer->reset();
			buffer->writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xf4116428);
			buffer->write<uint32>(0x000003f1); //
			SendPacket(buffer);

			break;
		}

	case 0x03: // RenameCharacter
		{
			uint64 nCharInstance = packet->buffer->read<uint32>();
			string cNewName = packet->buffer->read<string>();

			//

			break;
		}

	case 0xc3c5c31d: // RequestDeleteCharacter
		{
			uint32 nCharacterInstance = packet->buffer->read<uint32>();
			MySQLFunctions::DeleteCharacter(nCharacterInstance / 1009 - 1);
			SendCharacterList();

			break;
		}

	case 0x9847aed6: // VerifyLanguageSetting
		{
			// Inmy client lang is 04 PL 
			uint32 nClientLanguage = packet->buffer->read<uint32>();
			if(nClientLanguage==0) // if it's english client with enlish realm
			{
				//sendCharacterList(client);
			}
			else
			{
				buffer->reset();
				buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd4c59816); // Set the realm language to same as client ?
				buffer->write<uint32>(0x00000000); // server language 0 is english 
				SendPacket(buffer);
			}

			break;
		}

	case 0x06: // GetDimensionList
		{
			SendRealmList();

			break;
		}

	case 0x07: // RequestSuggestNickname
		{
			uint32 i_nRace = packet->buffer->read<uint32>();
			uint32 i_nSex = packet->buffer->read<uint32>();

			//

			break;
		}

	case 0x6a546d41: // GetStartupData
		{

			buffer->reset();
			buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x0c09ca25); // ???
			buffer->write<uint32>(0x000003f1);
			SendPacket(buffer);

			break;
		}
	case 0xdfd8518e:
		{
			SendCharacterList();
			SendRealmList();

			buffer->reset();
			buffer->writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x4f91a58c); // PlayerSetupComplete
			buffer->write<uint32>(0x01); // authstatus
			SendPacket(buffer);
			break;
		}

	default:
		{
			printf("Unknown Packet!\n%s\n%s\n\n", packet->toString().c_str(), packetBuffer->toString().c_str());

			break;
		}
	}

	m_bufferPool->disposeBuffer(buffer);
}