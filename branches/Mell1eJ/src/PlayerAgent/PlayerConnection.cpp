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

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

PlayerConnection::PlayerConnection(boost::asio::io_service& IOService, BufferPool* hp)
	: Connection(IOService, hp)
{
	//
}

void PlayerConnection::handlePacket(Packet &packet)
{
	//PacketBuffer *buffer = m_bufferPool->allocateBuffer(2000);
	PacketBuffer buffer(2000);

	switch(packet.opcode)
	{
	case 0x96CBE509:
		{
			//printf("Receive:\n%s\n\n", String::arrayToHexString(packet.packetBuffer->buffer, packet.packetBuffer->bufferLength).c_str());
			printf("Recv: Auth Init\n");

			InitAuth(packet);

			break;
		}
	case 0xDAD1D206:
		{
			//printf("Receive:\n%s\n\n", String::arrayToHexString(packet.packetBuffer->buffer, packet.packetBuffer->bufferLength).c_str());
			
			string unk1 = packet.data->read<string>();
			printf("Receive unknown String: %s\n", unk1.c_str());

			//send before charlist char-specific packets
			SendSmallCharList();

			break;
		}

	case 0xA3E1FE0D:
		{
			//printf("Receive:\n%s\n\n", String::arrayToHexString(packet.packetBuffer->buffer, packet.packetBuffer->bufferLength).c_str());
			
			SendCharacterList();
			SendRealmList();

			uint8 headerData[] = { 0x20, 0x8c };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
			
			PacketBuffer aBuffer(500);
			WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0xcbc6fc04);
			aBuffer.write<uint32>(1);
			SendPacket(aBuffer);

			break;
		}

	case 0xDD9EC209:
		{
			uint32 languageID = packet.data->read<uint32>();
			printf("Maybe report language\nLanguage: %i\n", languageID);

			/*
			Receive opcode: 0xdd9ec209
			Unknown Packet With Opcode: 0xDD9EC209
			00000000: 00 00 00 00                                      ....
			*/
			break;
		}

	case 0xDD85FB0E:
		{
			printf("Enter World\n");

			/*
			Receive opcode: 0xdd85fb0e
			Unknown Packet With Opcode: 0xDD85FB0E
			00000000: 01 00 07 E4 00 00 03 F1 00 02 65 6E 00 00 00 00  ..........en....
			00000010: 00 00 00 01 5F 00 00 00 00 00 00 00 00           ...._........
			*/

			break;
		}

	case 0x9CB1D10C:
		{
			printf("Maybe Create new Character\n");
			
			uint32 i_nDimID = packet.data->read<uint32>();

			RealmInfo realm;
			if(!MySQLFunctions::GetRealm(i_nDimID, realm)) // send error opcode, whatever
			{
				printf("No Realm found\n");
				break;
			}

			//gameClient.nClientInst = gameClient.charInfo.characterID + (i_nDimID * 0x01000000);
			uint32 nClientInst = gameClient.characterInfo.characterID + (i_nDimID * 0x01000000);
			gameClient.characterInfo.realmID = i_nDimID;

			uint8 headerData[] = { 0x20, 0xb9 };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };

			PacketBuffer aBuffer(500);
			WritePlayerHeader(aBuffer, sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x8bd89902);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(nClientInst); // gameClient.nClientInst
			aBuffer.write<uint32>(0);
			SendPacket(aBuffer);

			/*
			Receive opcode: 0x9cb1d10c
			Unknown Packet With Opcode: 0x9CB1D10C
			00000000: 00 00 00 01 00 00 03 F1 00 00 00 00 00 00 00 01  ................
			00000010: 5F 00 00 00 00 00 00 00 00                       _........
			*/
			break;
		}

	case 0xA4F2E303:
		{
			printf("Request for Server Addresses\n");
		
			RealmInfo realm;
			if(!MySQLFunctions::GetRealm(gameClient.characterInfo.realmID, realm)) // send error opcode, whatever
			{
				printf("No Realm found\n");
				break;
			}

			SendAgentServerAddress(realm.agentServerIPAddress, realm.agentServerPort);
			SendCSServerAddress(realm.csPlayerAgentIPAddress, realm.csPlayerAgentPort);
			SendWorldServerAddress(realm.worldServerIPAddress, realm.worldServerPort);
			
			break;
		}

	case 0x86979E0C:
		{
			/*
			Receive opcode: 0x86979e0c
			Unknown Packet With Opcode: 0x86979E0C
			00000000: 01 00 07 E4                                      ....
			*/
			uint32 charID = packet.data->read<uint32>();
			printf("Delete Character\nChar-ID: 0x%08x\n", charID);
			break;
		}

	default:
		{
			//printf("Unknown Packet!\n%s\n%s\n\n", packet.toString().c_str(), packetBuffer->toString().c_str());

			break;
		}
	}

	//m_bufferPool->disposeBuffer(buffer);
}