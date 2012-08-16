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

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

using namespace std;

vector<RealmInfo> ManagerConnection::realms;

ManagerConnection::ManagerConnection(boost::asio::io_service& IOService, BufferPool* hp) 
	: Connection(IOService, hp)
{
	if(realms.size() == 0)
		MysqlFunctions::GetRealms(realms);
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

void ManagerConnection::GetOfflineServer(FaolanManagerSenderReceivers type, RealmInfo &realm)
{
	//boost::mutex::scoped_lock lock(m_mutex);

	foreach(RealmInfo &realmInfo, realms)
	{
		if(type == UniverseAgentID && !realmInfo.universeAgentActive && !realmInfo.universeAgentInitializing)
		{
			realm = realmInfo;
			realmInfo.universeAgentInitializing = true;
		}
		else if(type == PlayerAgentID && !realmInfo.playerAgentActive && !realmInfo.playerAgentInitializing)
		{
			realm = realmInfo;
			realmInfo.playerAgentInitializing = true;
		}
		else if(type == WorldServerID && !realmInfo.worldServerActive && !realmInfo.worldServerInitializing)
		{
			realm = realmInfo;
			realmInfo.worldServerInitializing = true;
		}
		else if(type == AgentServerID && !realmInfo.agentServerActive && !realmInfo.agentServerInitializing)
		{
			realm = realmInfo;
			realmInfo.agentServerInitializing = true;
		}
		else if(type == CSPlayerServerID && !realmInfo.csPlayerAgentActive && !realmInfo.csPlayerAgentInitializing)
		{
			realm = realmInfo;
			realmInfo.csPlayerAgentInitializing = true;
		}
	}
}

void ManagerConnection::handlePacket(Packet &packet)
{
	//PacketBuffer *buffer = m_bufferPool.allocateBuffer(2000);
	PacketBuffer buffer(2000);

	FaolanManagerSenderReceivers sender = (FaolanManagerSenderReceivers)packet.sender;

	switch(packet.opcode)
	{
	case RequestRealmInfo:
		{
			//uint32 realmID = packet.data->read<uint32>();	

			RealmInfo realmInfo;
			GetOfflineServer(sender, realmInfo);

			WriteManagerHeader(buffer, FaolanManagerID, sender, RequestRealmInfoResponse);
			buffer.write<uint32>(realmInfo.realmID);

			switch(sender)
			{
			case UniverseAgentID:
				{
					buffer.write<string>(realmInfo.universeAgentIPAddress);
					buffer.write<uint32>(realmInfo.universeAgentPort);

					break;
				}
			case PlayerAgentID:
				{
					buffer.write<string>(realmInfo.playerAgentIPAddress);
					buffer.write<uint32>(realmInfo.playerAgentPort);

					break;
				}
			case WorldServerID:
				{
					buffer.write<string>(realmInfo.worldServerIPAddress);
					buffer.write<uint32>(realmInfo.worldServerPort);

					break;
				}
			case AgentServerID:
				{
					buffer.write<string>(realmInfo.agentServerIPAddress);
					buffer.write<uint32>(realmInfo.agentServerPort);

					break;
				}				
			case CSPlayerServerID:
				{
					buffer.write<string>(realmInfo.csPlayerAgentIPAddress);
					buffer.write<uint32>(realmInfo.csPlayerAgentPort);

					break;
				}	
			}

			SendPacket(buffer);

			break;
		}

	case RequestRegister:
		{
			string address = packet.data->read<string>();
			uint32 port = packet.data->read<uint32>();

			WriteManagerHeader(buffer, FaolanManagerID, (FaolanManagerSenderReceivers)packet.sender, RequestRegisterResponse);
			buffer.write<uint8>(true);
			SendPacket(buffer);

			break;
		}

	case ServerStatusChange:
		{
			uint32 realmID = packet.data->read<uint32>();
			bool status = packet.data->read<uint8>();

			// C++0X lambda expression
			RealmInfo realmInfo = *std::find_if(realms.begin(), realms.end(), [&](RealmInfo const& n)
			{
				return n.realmID == realmID;
			});

			switch(sender)
			{
			case UniverseAgentID:
				{
					realmInfo.universeAgentInitializing = false;
					realmInfo.universeAgentActive = status;

					break;
				}
			case PlayerAgentID:
				{
					realmInfo.playerAgentInitializing = false;
					realmInfo.playerAgentActive = status;

					break;
				}
			case WorldServerID:
				{
					realmInfo.worldServerInitializing = false;
					realmInfo.worldServerActive = status;

					break;
				}
			case AgentServerID:
				{
					realmInfo.agentServerInitializing = false;
					realmInfo.agentServerActive = status;

					break;
				}				
			case CSPlayerServerID:
				{
					realmInfo.csPlayerAgentInitializing = false;
					realmInfo.csPlayerAgentActive = status;

					break;
				}	
			}

			printf("%s: %s\n", FaolanManagerSenderReceiversText[sender], status ? "Active" : "Down");

			/*printf("Realm Status: UniverseAgent: %s, PlayerAgent: %s\nWorldServer: %s, AgentServer: %s, CSPlayerAgent: %s\n", 
				realmInfo.universeAgentActive ? "Active" : (realmInfo.universeAgentInitializing ? "Initializing" : "Down"),
				realmInfo.playerAgentActive   ? "Active" : (realmInfo.playerAgentInitializing   ? "Initializing" : "Down"),
				realmInfo.worldServerActive   ? "Active" : (realmInfo.worldServerInitializing   ? "Initializing" : "Down"),
				realmInfo.agentServerActive   ? "Active" : (realmInfo.agentServerInitializing   ? "Initializing" : "Down"),
				realmInfo.csPlayerAgentActive ? "Active" : (realmInfo.csPlayerAgentInitializing ? "Initializing" : "Down"));*/

			break;
		}

	case Pong:
		{
			uint64 ping = packet.data->read<uint64>();
			uint64 pong = packet.data->read<uint64>();

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