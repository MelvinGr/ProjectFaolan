/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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
	Database.GetCharacters(gameClient.nClientInst, &characters);
	
	PacketBuffer buffer(5000);
	buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xc414c5ef); // UpdateClientPlayerData
	buffer.write<uint32>(gameClient.characterInfo.accountID); // PlayerInstance
	buffer.write<uint32>((characters.size() + 1) * 1009); // number of characters
	
	for(uint32 i = 0; i < characters.size(); i++)
	{
		if(characters[i].level <= 0)
			continue;
		
		uint32 ClientInst = characters[i].characterID + (characters[i].realmID * 0x01000000);
		
		buffer.write<uint32>(ClientInst); // Charinstance
		buffer.write<uint32>(gameClient.characterInfo.accountID); // PlayerInstance
		buffer.write<uint32>(ClientInst); // Charinstance
		buffer.write<string>(characters[i].name); // charName
		buffer.write<uint32>(characters[i].realmID); // serverID
		buffer.write<uint32>(characters[i].sex); // Sex
		buffer.write<string>(characters[i].lastConnection); //last connection
		buffer.write<uint32>(0x00000000); // u1
		buffer.write<uint32>(characters[i].map); // playfield
		buffer.write<uint32>(characters[i].level); // Charlevel
		buffer.write<uint32>(characters[i].Class); // class
		buffer.write<uint32>(0x00000000); // u2
		buffer.write<uint32>(0x00000000); // u2
		buffer.write<uint32>(0x4bbafa33); // u3
		buffer.write<uint32>(0x00000002); // u4
		buffer.write<uint32>(characters[i].race); // race
		buffer.write<string>("en");
		buffer.write<uint32>(0x00000000); // u6
		buffer.write<uint32>(0x00000000); // u7
		buffer.write<string>("6f60ebba2cd4881d0393617a01f761b4"); // u8
	}
	
	buffer.write<uint32>(Settings.characterSlots); // Number of Char Slots -- default 8
	buffer.write<uint32>(0x000007e2);
	buffer.write<uint32>(0x00000002); // u9
	buffer.write<uint32>(0x0000000a); // u10
	buffer.write<uint32>(0x0000004a); // u11
	SendPacket(&buffer);
}

void PlayerConnection::SendRealmList()
{	
	vector<RealmInfo> realms;
	Database.GetRealms(&realms);
	
	PacketBuffer buffer(500);
	buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xf899b14c); // SetDimensionList
	buffer.write<uint32>(realms.size()); // Realmcount
	for(uint32 i = 0; i < realms.size(); i++)
	{
		buffer.write<uint32>(realms[i].realmID); // realmID
		buffer.write<uint32>(realms[i].onlineStatus); // onlineStatus - 2 = online, other num = offline
		buffer.write<uint32>(0x0007a120); //u1
		buffer.write<string>(realms[i].realmName); // realmname
		buffer.write<uint32>(0x00000002); //u2
		buffer.write<uint32>(0x00000000); //u3
		buffer.write<uint32>(0x00000000); //u4
		buffer.write<uint32>(0x00000000); // server full status 1 = full
		buffer.write<uint32>(realms[i].loadStatus); //load status -- 0 = medium load 1 = medium load 2 = heavy load 3 = full
		buffer.write<uint32>(0x00000000); //u7
		buffer.write<uint32>(0x00000000); //u8
		buffer.write<uint16>(realms[i].realmType); // realmtype - 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
		buffer.write<uint32>(0x00000000); //u9
	}
	SendPacket(&buffer);
}

/////////////////////

void PlayerConnection::onRead(const boost::system::error_code &e, size_t bytesTransferred)
{
	if(e)
	{
		printf("Client disconnected\n");
		return;
	}

	//PacketBuffer *buffer = m_bufferPool->allocateBuffer(500);

	Packet packet(&m_readBuffer);
	switch(packet.opcode)
	{
		case 0x9cb2cb03: // Authenticate
		{
			gameClient.nClientInst = packet.data.read<uint32>();
			gameClient.nCookie = packet.data.read<uint32>();
			gameClient.characterInfo.characterID = gameClient.nCookie;
			gameClient.characterInfo.accountID = gameClient.nClientInst;
			
			if(false)  //isCharacterLoggedIn(gameClient.nClientInst))
			 {
			 PacketBuffer buffer(500);
			 buffer.writeHeader("ServerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd4063ca0); // Playerloggedin
			 buffer.write<uint32>(0x0000000b);
			 SendPacket(&buffer);
			 
			 break;
			 }
			
			printf("Client-Cookie-> %i\n", gameClient.nCookie);
			
			if(gameClient.nCookie == Database.getAccountCookie(gameClient.nClientInst))
			{
				PacketBuffer buffer(500);
				buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5dc18991); // AuthenticateAck
				buffer.write<uint32>(0x01); // nStatus
				SendPacket(&buffer);
			}
			else
			{
				PacketBuffer buffer(500);
				buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5dc18991); // AuthenticateAck
				buffer.write<uint32>(0xff); // nStatus
				SendPacket(&buffer);
			}
			
			break;
		}
			
		case 0xBE735486: // CreateCharacter
		{
			printf("Get second char creation pack\n");
			uint32 i_nDimID = packet.data.read<uint32>();
			
			uint32 unk1 = packet.data.read<uint32>(); // dont know if these are correct values
			uint32 unk2 = packet.data.read<uint32>();
			uint8 unk3 = packet.data.read<uint8>();
			
			RealmInfo realm;			
			if(!Database.GetRealm(i_nDimID, &realm)) // send error opcode, whatever
			{
				printf("Could not find Realm with ID: %u in the Databse!\n", i_nDimID);
				break;
			}
			
			//gameClient.characterInfo.accountID = gameClient.nClientInst;
			//gameClient.nClientInst += (i_nDimID * 0x01000000);
			gameClient.nClientInst = gameClient.characterInfo.characterID + (i_nDimID * 0x01000000);
			
			//gameClient.characterInfo.characterID = (Database.getNewCharacterID() + 1) * 1009;
			if(gameClient.characterInfo.characterID == -1) // send error opcode, whatever
				break;
			
			//Database.insertEmptyChar(client);
			//gameClient.nClientInst = Network::MakeClientInst(realm.realmID, gameClient.characterInfo.characterID);
			
			PacketBuffer buffer(500);
			/*
			 buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			 buffer.write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			 buffer.write<uint16>(realm.csPlayerAgentPort); // CSport
			 buffer.write<uint32>(gameClient.characterInfo.characterID);
			 buffer.write<uint32>(0x0000c350);
			 buffer.write<uint32>(gameClient.nClientInst);
			 SendPacket(&buffer);
			 
			 buffer.reset();
			 //*/
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer.write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer.write<uint16>(realm.agentServerPort);
			//buffer.write<uint32>(gameClient.characterInfo.characterID);
			buffer.write<uint32>(gameClient.characterInfo.accountID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer.write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer.write<uint32>(gameClient.characterInfo.accountID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(gameClient.characterInfo.accountID); //
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst); //
			buffer.write<uint8>(0x62); //
			buffer.write<uint32>(0x0000c79c); //
			buffer.write<uint32>(gameClient.characterInfo.map); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000001); //
			buffer.write<uint32>(0x00009c50); //
			buffer.write<uint32>(0x00036bc1); // cookie
			SendPacket(&buffer);
			/*
			 buffer.reset();
			 buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			 buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			 buffer.write<uint16>(realm.worldServerPort); // game port
			 buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			 buffer.write<uint16>(realm.worldServerPort); // game port
			 buffer.write<uint32>(gameClient.characterInfo.characterID); //
			 buffer.write<uint32>(0x0000c350);
			 buffer.write<uint32>(gameClient.nClientInst); //
			 buffer.write<uint8>(0x62); //
			 buffer.write<uint32>(0x0000c79c); //
			 buffer.write<uint32>(0x00000fa0); //StartMAP Stygian Galley
			 buffer.write<uint32>(0x00000000); //
			 buffer.write<uint32>(0x00000000); //
			 buffer.write<uint32>(0x00000001); //
			 buffer.write<uint32>(0x00009c50); //
			 buffer.write<uint32>(0x00036bc1); // cookie
			 SendPacket(&buffer);
			 
			 buffer.reset();
			 buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xf4116428);
			 buffer.write<uint32>(0x000007e2); //
			 buffer.write<uint32>(0x00000001); //
			 buffer.write<uint32>(0x00000001); //
			 SendPacket(&buffer);
			 //*/
			break;
		}
			
		case 0xEF616EB6: // LoginCharacter:
		{
			gameClient.nClientInst = packet.data.read<uint32>();
			uint32 unk = packet.data.read<uint32>();
			string cClientLanguage = packet.data.read<string>();
			
			uint32 unk1 = packet.data.read<uint32>(); // dont know if these are correct values
			uint16 unk2 = packet.data.read<uint16>();
			uint8 unk3 = packet.data.read<uint8>();

			uint32 charID = gameClient.nClientInst & 0x00ffffff;		
			if(!Database.GetCharacter(charID, &gameClient.characterInfo)) // send error opcode, whatever
			{
				printf("Could not find Character with ID: %u in the Database!\n", charID);
				break;
			}
			
			RealmInfo realm;			
			if(!Database.GetRealm(gameClient.characterInfo.realmID, &realm)) // send error opcode, whatever
			{
				printf("Could not find Realm with ID: %u in the Database!\n", gameClient.characterInfo.realmID);
				break;
			}
			
			//gameClient.nClientInst = gameClient.characterInfo.accountID + (gameClient.characterInfo.realmID * 0x01000000);
			gameClient.nClientInst = gameClient.characterInfo.characterID + (gameClient.characterInfo.realmID * 0x01000000);
			
			PacketBuffer buffer(500);
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer.write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer.write<uint32>(gameClient.characterInfo.accountID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer.write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer.write<uint16>(realm.agentServerPort);
			buffer.write<uint32>(gameClient.characterInfo.accountID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(gameClient.characterInfo.accountID); //
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst); //
			buffer.write<uint8>(0x62); //
			buffer.write<uint32>(0x0000c79c); //
			buffer.write<uint32>(gameClient.characterInfo.map); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000001); //
			buffer.write<uint32>(0x00009c50); //
			buffer.write<uint32>(0x00036bc1); // cookie
			SendPacket(&buffer);
			
			break;
		}
		case 0xCA2C4E5E:
		{
			gameClient.characterInfo.realmID = packet.data.read<uint32>();
			/*
			 000006E8  00 00 00 42 4b 7d b2 74  00 0b 50 6c 61 79 65 72 ...BK}.t ..Player
			 000006F8  41 67 65 6e 74 00 00 00  06 00 00 00 00 00 0f 50 Agent... .......P
			 00000708  6c 61 79 65 72 49 6e 74  65 72 66 61 63 65 59 47 layerInt erfaceYG
			 00000718  c8 29 00 00 00 00 23 36  05 b9 00 00 c3 50 01 0a .)....#6 .....P..
			 00000728  82 7e 00 00 00 00
			 */
			PacketBuffer buffer(500);
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x233605b9); // CSServerConnectReady
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.characterInfo.characterID);
			buffer.write<uint32>(0);
			SendPacket(&buffer);
			
			break;
		}
		case 0x3c7c926c:
		{
			
			printf("Receive Create Char\n");
			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == gameClient.characterInfo.realmID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}
			
			if(realm == 0) // send error opcode, whatever
			{
				printf("Realm-Error!!!\n");
				break;
			}
			
			PacketBuffer buffer(500);
			
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x5aed2a60); // CSServerConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.csPlayerAgentIPAddress.c_str()))); // CSPlayer
			buffer.write<uint16>(realm.csPlayerAgentPort); // CSport
			buffer.write<uint32>(gameClient.characterInfo.characterID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x23a632fa);
			buffer.write<uint32>(htonl(inet_addr(realm.agentServerIPAddress.c_str()))); // i_nClientAgentAddress
			buffer.write<uint16>(realm.agentServerPort);
			buffer.write<uint32>(gameClient.characterInfo.characterID);
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.nClientInst);
			SendPacket(&buffer);
			
			buffer.reset();
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd8b66c79); // LoginConnectReady
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(htonl(inet_addr(realm.worldServerIPAddress.c_str()))); // i_nGameAddr
			buffer.write<uint16>(realm.worldServerPort); // game port
			buffer.write<uint32>(gameClient.nClientInst);//gameClient.characterInfo.characterID); //
			buffer.write<uint32>(0x0000c350);
			buffer.write<uint32>(gameClient.characterInfo.characterID);//gameClient.nClientInst); //
			buffer.write<uint8>(0x62); //
			buffer.write<uint32>(0x0000c79c); //
			buffer.write<uint32>(gameClient.characterInfo.map); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000000); //
			buffer.write<uint32>(0x00000001); //
			buffer.write<uint32>(0x00009c50); //
			buffer.write<uint32>(0x00036bc1); // cookie
			SendPacket(&buffer);
			
			buffer.reset();
			//*/
			
			buffer.writeHeader("ServerInterface", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xf4116428);
			buffer.write<uint32>(0x000003f1); //
			SendPacket(&buffer);
			//*/
			break;
		}
			
		case 0x03: // RenameCharacter
		{
			uint64 nCharInstance = packet.data.read<uint32>();
			string cNewName = packet.data.read<string>();
			
			//
			
			break;
		}
			
		case 0xc3c5c31d: // RequestDeleteCharacter
		{
			uint32 nCharacterInstance = packet.data.read<uint32>();
			printf("REQUEST DELETE CHAR\n\n");
			Database.deleteCharacter(nCharacterInstance / 1009 - 1);
			sendCharacterList(client);
			
			break;
		}
			
		case 0x9847aed6: // VerifyLanguageSetting
		{
			// Inmy client lang is 04 PL 
			uint32 nClientLanguage = packet.data.read<uint32>();
			if(nClientLanguage==0) // if it's english client with enlish realm
			{
				//sendCharacterList(client);
			}
			else
			{
				PacketBuffer buffer(500);
				buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0xd4c59816); // Set the realm language to same as client ?
				buffer.write<uint32>(0x00000000); // server language 0 is english 
				SendPacket(&buffer);
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
			uint32 i_nRace = packet.data.read<uint32>();
			uint32 i_nSex = packet.data.read<uint32>();
			
			//
			
			break;
		}
			
		case 0x6a546d41: // GetStartupData
		{
			
			PacketBuffer buffer(500);
			buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x0c09ca25); // ???
			buffer.write<uint32>(0x000003f1);
			SendPacket(&buffer);
			
			//Then wait
			
			//sendCharacterList(client);
			//sendRealmList(client);
			/*
			 buffer.reset();
			 buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x4f91a58c); // PlayerSetupComplete
			 buffer.write<uint32>(0x01); // authstatus
			 SendPacket(&buffer);
			 //*/
			
			break;
		}
			
		case 0xdfd8518e:
		{
			SendCharacterList();
			SendRealmList();
			
			PacketBuffer buffer(500);
			buffer.writeHeader("PlayerAgent", "PlayerInterface", 1, 0, gameClient.nClientInst, 0, 0x4f91a58c); // PlayerSetupComplete
			buffer.write<uint32>(0x01); // authstatus
			SendPacket(&buffer);
			
			break;
		}
			
		default:
		{
			printf("Unknown Packet With Opcode: 0x%08X\n", packet.opcode);
			printf("%s\n\n", String::arrayToHexString((uint8*)packet.data.constCharBuffer(), packet.data.size()).c_str());
			
			break;
		}
	}
	
	//m_bufferPool->disposeBuffer(buffer);
	AsyncRead();
}

void PlayerConnection::onWrite(const boost::system::error_code &e)
{
	if(e)
	{
		printf("socket write problem \n");
		boost::system::error_code ignored_ec;
		m_socket.shutdown(boost::asio::ip::tcp::socket::shutdown_both, ignored_ec);
	}
}