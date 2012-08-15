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

#include "ManagerConnection.h"

#include "../Common/FaolanManager.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

ManagerConnection::ManagerConnection(boost::asio::io_service& IOService, BufferPool* hp) 
	: Connection(IOService, hp)
{
	//
}

void ManagerConnection::start()
{
	Connection::start();

	//
}

void ManagerConnection::stop()
{
	Connection::stop();

	//
}

void ManagerConnection::handlePacket(Packet &packet)
{
	//PacketBuffer *buffer = m_bufferPool.allocateBuffer(2000);
	PacketBuffer buffer(2000);

	switch(packet.opcode)
	{
	case RequestNewOfflineRealm:
		{
			//uint32 realmID = packet.data->read<uint32>();

			//uint32 clientID = rand();

			vector<RealmInfo> realms;
			MysqlFunctions::GetRealms(realms);

			WriteManagerHeader(buffer, FaolanManager, (FaolanManagerSenderReceivers)packet.sender, RequestNewOfflineRealmResponse);

			foreach(const RealmInfo &realmInfo, realms)
			{
				if(realmInfo.onlineStatus == 2)
					continue;

				buffer.write<uint32>(realmInfo.realmID);
				break;
			}

			SendPacket(buffer);

			break;
		}

	case RequestRealmWorldServerInfo:
		{
			uint32 realmID = packet.data->read<uint32>();	

			RealmInfo realmInfo;
			MysqlFunctions::GetRealm(realmID, realmInfo);

			WriteManagerHeader(buffer, FaolanManager, (FaolanManagerSenderReceivers)packet.sender, RequestRealmWorldServerInfoResponse);
			buffer.write<string>(realmInfo.worldServerIPAddress);
			buffer.write<uint32>(realmInfo.worldServerPort);
			SendPacket(buffer);

			break;
		}

	case RequestRealmAgentServerInfo:
		{
			uint32 realmID = packet.data->read<uint32>();

			RealmInfo realmInfo;
			MysqlFunctions::GetRealm(realmID, realmInfo);

			WriteManagerHeader(buffer, FaolanManager, (FaolanManagerSenderReceivers)packet.sender, RequestRealmAgentServerInfoResponse);
			buffer.write<string>(realmInfo.agentServerIPAddress);
			buffer.write<uint32>(realmInfo.agentServerPort);
			SendPacket(buffer);

			break;
		}

	case RequestRealmCSPlayerServerInfo:
		{
			uint32 realmID = packet.data->read<uint32>();

			RealmInfo realmInfo;
			MysqlFunctions::GetRealm(realmID, realmInfo);

			WriteManagerHeader(buffer, FaolanManager, (FaolanManagerSenderReceivers)packet.sender, RequestRealmCSPlayerServerInfoResponse);
			buffer.write<string>(realmInfo.csPlayerAgentIPAddress);
			buffer.write<uint32>(realmInfo.csPlayerAgentPort);
			SendPacket(buffer);

			break;
		}

	case RequestRegister:
		{
			string address = packet.data->read<string>();
			uint32 port = packet.data->read<uint32>();

			WriteManagerHeader(buffer, FaolanManager, (FaolanManagerSenderReceivers)packet.sender, RequestRegisterResponse);
			buffer.write<uint8>(true);
			SendPacket(buffer);

			break;
		}

	case ServerStatusChange:
		{
			//

			break;
		}

	case Pong:
		{
			//

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