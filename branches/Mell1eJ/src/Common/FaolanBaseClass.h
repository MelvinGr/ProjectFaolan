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

#ifndef FaolanBaseClass_H
#define FaolanBaseClass_H

#include "Common.h"

#include <iostream>

#include <boost/bind.hpp>
#include <boost/function.hpp>
#include <boost/thread.hpp>

#include "Network.h"
#include "Configuration.h"
#include "FaolanManager.h"
#include "InterServerConnection.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "MysqlFunctions.h"
#endif

template <class ConnectionAcceptorT> class FaolanBaseClass
{
	Network network;
	boost::asio::io_service ioService;
	InterServerConnection* internalConnection;

	boost::mutex m_mutex;
	boost::condition_variable m_condition;

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	void handlePacket(Packet &packet)
	{
		switch(packet.opcode)
		{
		case RequestRealmInfoResponse:
			{
				realmID = packet.data->read<uint32>();
				string address = packet.data->read<string>();
				uint32 port = packet.data->read<uint32>();

				network.createConnectionAcceptor<ConnectionAcceptorT>(address, port, Config.GetValue<size_t>("demuxercount"));
				DoServerStatusChange(true);

				m_condition.notify_all();

				break;
			}

		case Ping:
			{
				uint64 ping = packet.data->read<uint64>();

				PacketBuffer packetBuffer(100);
				WriteManagerHeader(packetBuffer, sender, FaolanManagerID, Pong);
				packetBuffer.write<uint64>(ping);
				packetBuffer.write<uint64>(time(0));
				internalConnection->SendPacket(packetBuffer);

				break;
			}

		default:
			{
				//

				break;
			}
		}
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

public:
	FaolanManagerSenderReceivers sender;
	uint32 realmID;

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	FaolanBaseClass(int32 argc, int8* argv[], FaolanManagerSenderReceivers _sender)
		: internalConnection(0), sender(_sender), realmID(0)
	{
		printf("%s", FaolanBanner);

		srand((uint32)time(0));

		Config.parseConfigFile("FaolanConfig.cfg");
		Config.parseCommandLine(argc, argv);

		MysqlDatabase::createInstance(Config.GetValue<size_t>("demuxercount"), Config.GetValue<string>("dbusername"), 
			Config.GetValue<string>("dbhost"), Config.GetValue<string>("dbpassword"), Config.GetValue<string>("dbname"), 
			Config.GetValue<uint32>("dbport"));
	}

	~FaolanBaseClass()
	{
		MysqlDatabase::destroy();

		if(internalConnection)
			delete internalConnection;
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	void Start(bool isManager = false)
	{
		if(!MysqlDB->start())
			throw runtime_error("Could not connect to the Database!");

		if(isManager)
		{
			network.createConnectionAcceptor<ConnectionAcceptorT>(Config.GetValue<string>("faolanmanageripaddress"),
				Config.GetValue<uint32>("faolanmanagerport"), Config.GetValue<size_t>("demuxercount"));

			network.wait();
		}
		else
		{
			internalConnection = new InterServerConnection(ioService,
				Config.GetValue<string>("faolanmanageripaddress"), Config.GetValue<uint32>("faolanmanagerport"), sender);

			internalConnection->HandlePacketCallback = boost::bind(&FaolanBaseClass::handlePacket, this, _1);

			boost::thread icThread(boost::bind(&InterServerConnection::start, internalConnection));

			if (internalConnection->waitUntilConnected())
				printf("Connected to the Manager!\n");
			else
				throw runtime_error("Could not connect to the Manager!");

			///////

			DoRequestRealmInfo();

			boost::mutex::scoped_lock lock(m_mutex);
			m_condition.wait(lock);

			network.wait();
		}
	}

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

	void DoServerStatusChange(bool status)
	{
		PacketBuffer buffer(100);
		WriteManagerHeader(buffer, sender, FaolanManagerID, ServerStatusChange);
		buffer.write<uint32>(realmID);
		buffer.write<uint8>(status);
		internalConnection->SendPacket(buffer);
	}

	void DoRequestRealmInfo()
	{
		PacketBuffer buffer(2000);
		WriteManagerHeader(buffer, sender, FaolanManagerID, RequestRealmInfo);
		buffer.write<uint8>(0); // dummy
		internalConnection->SendPacket(buffer);
	}

	/*void registerWithManager()
	{
	PacketBuffer buffer(2000);
	WriteManagerHeader(buffer, sender, FaolanManager, RequestRegister);
	buffer.write<string>(m_loginServerAddress);
	buffer.write<uint32>(m_port);
	SendPacket(buffer);
	}

	void onRegistrationStatus(Packet &packet)
	{
	m_registered = packet.data->read<uint8>();

	if (m_registered)
	printf("Server completed registration on auth server\n");
	else
	printf("Problems at registration on auth server\n");

	m_condition.notify_all();
	}*/
};

#endif