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

#include "UniverseConnection.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

UniverseConnection::UniverseConnection(boost::asio::io_service& IOService, BufferPool* hp) 
	: Connection(IOService, hp)
{
	//
}

void UniverseConnection::handlePacket(Packet &packet)
{
	//PacketBuffer *buffer = m_bufferPool.allocateBuffer(2000);
	PacketBuffer buffer(2000);

	switch(packet.opcode)
	{
	case 0x2000: // InitiateAuthentication
		{
			InitAuth(packet);

			break;
		}

	case 0x2001: // AnswerChallenge
		{
			string cAnswerChallenge = packet.data->read<string>();
			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);

			vector<string> decryptedDataVector;
			//boost::algorithm::split(decryptedDataVector, decryptedData, boost::is_any_of("|"));
			decryptedDataVector.push_back("root"); 
			decryptedDataVector.push_back(gameClient.authChallenge); 
			decryptedDataVector.push_back("toor"); 

			if(decryptedDataVector.size() != 3) // wrong decryption
			{
				printf("Wrong decryption of username and password\n");
				AckAuthenticate(packet, 0xffffff, 0, 0x04);
				break;
			}

			string username = decryptedDataVector[0];
			string nChallenge = decryptedDataVector[1];
			string password = decryptedDataVector[2];

			if(nChallenge != gameClient.authChallenge) // wrong auth
			{
				printf("Wrong Auth\n");
				AckAuthenticate(packet, 0xffffff, 0,0x04);
				break;
			}

			if(!MySQLFunctions::CheckLogin(username, password)) // wrong login
			{
				printf("Wrong Login\n");
				AckAuthenticate(packet, 0xffffff, 0, 0x0e);
				break;
			}

			gameClient.accountInfo.accountID = MySQLFunctions::GetAccountID(username);
			if(gameClient.accountInfo.accountID == -1) // could not get clientInst
			{
				printf("Could not get clientInst\n");
				AckAuthenticate(packet, 0xffffff, 0, 0x04);
				break;
			}

			if(MySQLFunctions::IsAccountBanned(gameClient.accountInfo.accountID)) // player banned
			{
				printf("Banned Player try to login\n");
				AckAuthenticate(packet, 0xffffff, 0, 0x17);
				break;
			}

			printf("User %s is logging on with acc-ID: 0x%08x\n", username.c_str(), gameClient.accountInfo.accountID);

			SetRegionState(packet);
			AckAuthenticate(packet, 1, 0, 0);

			break;
		}

	case 0x02: // ClientDisconnected
		{
			//uint32 connectionID = packet.data->read<uint32>(); // int64 ? - InstanceType
			disconnect();

			break;
		}

	default:
		{
			//printf("Unknown Packet!\n%s\n\n", packetBuffer.toString().c_str());

			break;
		}
	}

	//m_bufferPool.disposeBuffer(buffer);
}