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

#include "InternalConnection.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

InternalConnection::InternalConnection(boost::asio::io_service& IOService, BufferPool* hp, 
									const std::string& loginServerAddress, uint32 port, FaolanManagerSenderReceivers _sender) 
	: InterServerConnection(IOService, loginServerAddress, port, _sender)
{
	//
}

void InternalConnection::handlePacket(Packet &packet)
{
	//PacketBuffer *buffer = m_bufferPool.allocateBuffer(2000);
	PacketBuffer buffer(2000);

	switch(packet.opcode)
	{
	//

	default:
		{
			//printf("Unknown Packet!\n%s\n\n", packetBuffer.toString().c_str());

			break;
		}
	}

	//m_bufferPool.disposeBuffer(buffer);
}