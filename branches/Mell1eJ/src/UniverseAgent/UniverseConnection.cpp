/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

void UniverseConnection::handlePacket(PacketBuffer *m_packetBuffer, Packet* m_packet)
{
	PacketBuffer *m_writeBuffer = m_bufferPool->allocateBuffer(2000);
	switch(m_packet->opcode)
	{
	case 0x00: // InitiateAuthentication
		{
			string cDevName = m_packet->m_buffer->read<string>();
			string cUserName = m_packet->m_buffer->read<string>();
			uint32 nGameID = m_packet->m_buffer->read<uint32>();

			m_writeBuffer->reset();
			m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x00); // Challenge
			m_writeBuffer->write<string>(gameClient.authChallenge);
			SendPacket(m_writeBuffer);

			break;
		}

	case 0x01: // AnswerChallenge
		{
			string cAnswerChallenge = m_packet->m_buffer->read<string>();
			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);

			vector<string> decryptedDataVector;
			boost::algorithm::split(decryptedDataVector, decryptedData, boost::is_any_of("|"));

			if(decryptedDataVector.size() != 3) // wrong decryption
			{
				m_writeBuffer->reset();
				m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				m_writeBuffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(m_writeBuffer);

				break;
			}

			string username = decryptedDataVector[0];
			string nChallenge = decryptedDataVector[1];
			string password = decryptedDataVector[2];

			if(nChallenge != gameClient.authChallenge) // wrong auth
			{
				m_writeBuffer->reset();
				m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				m_writeBuffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(m_writeBuffer);

				break;
			}
				
			if(!MySQLFunctions::CheckLogin(username, password)) // wrong login
			{
				m_writeBuffer->reset();
				m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01); // AckAuthenticate
				m_writeBuffer->write<uint32>(0x00); // nAuthStatus
				m_writeBuffer->write<uint64>(0x00); // cPlayerID
				m_writeBuffer->write<uint32>(0x00); // uCookie
				m_writeBuffer->write<uint32>(0x00); // eReason
				m_writeBuffer->write<uint16>(0x0E); // error code: invalid username/password
				SendPacket(m_writeBuffer);

				break;
			}
			
			gameClient.nClientInst = MySQLFunctions::GetAccountID(username);
			if(gameClient.nClientInst == -1) // could not get clientInst
			{
				m_writeBuffer->reset();
				m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x02); // LoginProblem
				m_writeBuffer->write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				SendPacket(m_writeBuffer);

				break;
			}

			if(MySQLFunctions::IsAccountBanned(gameClient.nClientInst)) // player banned
			{
				m_writeBuffer->reset();
				m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01);
				m_writeBuffer->write<uint32>(0x00); // nAuthStatus
				m_writeBuffer->write<uint64>(0x00); // cPlayerID
				m_writeBuffer->write<uint32>(0x00); // uCookie
				m_writeBuffer->write<uint32>(0x00); // eReason
				m_writeBuffer->write<uint16>(0x17); // error code: account freezed
				SendPacket(m_writeBuffer);

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

			m_writeBuffer->reset();
			m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x05); // SetRegionState
			m_writeBuffer->write(real, sizeof(real));
			SendPacket(m_writeBuffer);

			uint32 cookie = MySQLFunctions::GetNewCharacterId();
			string playerAgentIPAddress = Config.playerAgentAddress + ":" + String::toString(Config.playerAgentPort);

			MySQLFunctions::SetAccountCookie(gameClient.nClientInst, cookie);
			MySQLFunctions::UpdateLastInfo(gameClient.nClientInst, gameClient.ipAddress);

			m_writeBuffer->reset();
			m_writeBuffer->writeHeader("UniverseAgent", "UniverseInterface", 1, 0, gameClient.nClientInst, 0, 0x01); // AckAuthenticate
			m_writeBuffer->write<uint32>(0x01); // nAuthStatus
			m_writeBuffer->write<uint64>(gameClient.nClientInst); // cPlayerID
			m_writeBuffer->write<string>(playerAgentIPAddress); // cTerritoryManagerAddr
			m_writeBuffer->write<uint32>(cookie); // uCookie
			m_writeBuffer->write<uint32>(0x00); // eReason
			SendPacket(m_writeBuffer);

			break;
		}

	case 0x02: // ClientDisconnected
		{
			//uint32 connectionID = m_packet->m_buffer->read<uint32>(); // int64 ? - InstanceType
			disconnect();

			break;
		}

	default:
		{
			printf("Unknown Packet!\n");
			printf("%s\n\n", m_packetBuffer->toString().c_str());

			break;
		}
	}

	m_bufferPool->disposeBuffer(m_writeBuffer);
}