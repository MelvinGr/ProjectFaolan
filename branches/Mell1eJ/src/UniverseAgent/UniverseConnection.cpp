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

#include "UniverseConnection.h"

uint32 UniverseConnection::connectionCount = 0;

UniverseConnection::UniverseConnection(boost::asio::io_service& IOService, BufferPool* hp) : Connection(IOService, hp), m_connectionID(0)
{
	//
}

UniverseConnection::~UniverseConnection()
{
	//
}

void UniverseConnection::start()
{
	m_connectionID = connectionCount;

	printf("New connection accepted (m_connectionID: %u)\n", m_connectionID);
	connectionCount++;

	AsyncRead();
}

void UniverseConnection::handlePacket(PacketBuffer *packetBuffer, Packet* packet)
{
	PacketBuffer *buffer = m_bufferPool->allocateBuffer(2000);
	switch(packet->opcode)
	{
	case 0x00: // InitiateAuthentication
		{
			string cDevName = packet->buffer->read<string>();
			string cUserName = packet->buffer->read<string>();
			uint32 nGameID = packet->buffer->read<uint32>();

			buffer->reset();
			buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x00); // Challenge
			buffer->write<string>(gameClient.authChallenge);
			SendPacket(buffer);

			break;
		}

	case 0x01: // AnswerChallenge
		{
			string cAnswerChallenge = packet->buffer->read<string>();
			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);

			vector<string> decryptedDataVector;
			boost::algorithm::split(decryptedDataVector, decryptedData, boost::is_any_of("|"));

			if(decryptedDataVector.size() != 3) // wrong decryption
			{
				buffer->reset();
				buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				buffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(buffer);

				break;
			}

			string username = decryptedDataVector[0];
			string nChallenge = decryptedDataVector[1];
			string password = decryptedDataVector[2];

			if(nChallenge != gameClient.authChallenge) // wrong auth
			{
				buffer->reset();
				buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				buffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(buffer);

				break;
			}
				
			if(!MySQLFunctions::CheckLogin(username, password)) // wrong login
			{
				buffer->reset();
				buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01); // AckAuthenticate
				buffer->write<uint32>(0x00); // nAuthStatus
				buffer->write<uint64>(0x00); // cPlayerID
				buffer->write<uint32>(0x00); // uCookie
				buffer->write<uint32>(0x00); // eReason
				buffer->write<uint16>(0x0E); // error code: invalid username/password
				SendPacket(buffer);

				break;
			}
			
			gameClient.nClientInst = MySQLFunctions::GetAccountID(username);
			if(gameClient.nClientInst == -1) // could not get clientInst
			{
				buffer->reset();
				buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				buffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(buffer);

				break;
			}

			if(MySQLFunctions::IsAccountBanned(gameClient.nClientInst)) // player banned
			{
				buffer->reset();
				buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01);
				buffer->write<uint32>(0x00); // nAuthStatus
				buffer->write<uint64>(0x00); // cPlayerID
				buffer->write<uint32>(0x00); // uCookie
				buffer->write<uint32>(0x00); // eReason
				buffer->write<uint16>(0x17); // error code: account freezed
				SendPacket(buffer);

				break;
			}

			const uint8 real[] =
			{
				0x00, 0x00, 0x06, 0x08, 
				0x00, 0x00, 0x00, 0x02, 
				0x00, 0x00, 0x00, 0x00, 
				0x01, 0x01, 0x01, 0x01, 
				0x3f, 0x80, 
				0x00, 0x00, 
				0x3f, 0x80, 
				0x00, 0x00, 0x01, 0x01, 
				0x00, 0x00, 0x00, 0x00, 
				0x09
			};

			buffer->reset();
			buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x05); // SetRegionState
			buffer->write(real, sizeof(real));
			SendPacket(buffer);

			//uint32 cookie = MySQLFunctions::GetNewCharacterId();
			string playerAgentIPAddress = Config.playerAgentAddress + ":" + String::toString(Config.playerAgentPort);

			//MySQLFunctions::SetAccountCookie(gameClient.nClientInst, cookie);
			MySQLFunctions::UpdateLastInfo(gameClient.nClientInst, gameClient.ipAddress);

			buffer->reset();
			buffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01); // AckAuthenticate
			buffer->write<uint32>(0x01); // nAuthStatus
			buffer->write<uint64>(gameClient.nClientInst); // cPlayerID
			buffer->write<string>(playerAgentIPAddress); // cTerritoryManagerAddr
			buffer->write<uint32>(0xDEADBEEF); // uCookie
			buffer->write<uint32>(0x00); // eReason
			SendPacket(buffer);

			break;
		}

	case 0x02: // ClientDisconnected
		{
			//uint32 connectionID = packet->buffer->read<uint32>(); // int64 ? - InstanceType
			disconnect();

			break;
		}

	default:
		{
			printf("Unknown Packet!\n%s\n\n", packetBuffer->toString().c_str());

			break;
		}
	}

	m_bufferPool->disposeBuffer(buffer);
}