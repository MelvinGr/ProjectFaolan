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

#include <boost/lexical_cast.hpp>

using namespace std;

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
	case 0x2000: // InitiateAuthentication
		{
			string cUserName = packet->buffer->read<string>();
			uint32 nGameID = packet->buffer->read<uint32>();

			buffer->reset();
			buffer->writeHeader(0x00000016, 0x0A070DA0, 0xDB4D6010, 0x0112090D, 0x5440380C, 0x10E4);

			buffer->write<uint32>(0xA5412000);
			buffer->write<string>("704c38cd38334cf4862515a14758f4b9");

			SendPacket(buffer);

			break;
		}

	case 0x2001: // AnswerChallenge
		{
			string cAnswerChallenge = packet->buffer->read<string>();
			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);

			vector<string> decryptedDataVector;
			boost::algorithm::split(decryptedDataVector, decryptedData, boost::is_any_of("|"));

			buffer->reset();
			buffer->writeHeader(0x00000018, 0x0A070DA0, 0xDB4D6010, 0x01120B0D, 0x5440380C, 0x10EC);

			buffer->write<uint32>(0xEB80DE03);
			buffer->write<uint32>(0x20050101); //
			buffer->write<uint32>(0x01013F80); //
			buffer->write<uint32>(0x00003F80);
			buffer->write<uint32>(0x00000101);
			buffer->write<uint8>(0x00);
		
			SendPacket(buffer);

			///////////////////////////////////////////////////////////////////

			buffer->reset();
			buffer->writeHeader(0x00000018, 0x0A070DA0, 0xDB4D6010, 0x01120B0D, 0x5440380C, 0x10EC);

			buffer->write<uint32>(0xEB80DE03); 
			buffer->write<uint32>(0x20010000); 
			buffer->write<uint32>(0x00010000);
			buffer->write<uint32>(0x27123BC0);
			buffer->write<uint16>(0x35EC);
			buffer->write<string>("127.0.0.1:7001");
			buffer->write<uint32>(0x38B8D44F); // cookie
			buffer->write<uint32>(0x00000000); // reason

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