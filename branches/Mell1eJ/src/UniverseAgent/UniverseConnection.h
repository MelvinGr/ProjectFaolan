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

#ifndef UNIVERSECONNECTION_H
#define UNIVERSECONNECTION_H

#include <iostream>

#include <boost/enable_shared_from_this.hpp>
#include <boost/bind.hpp>
#include <boost/algorithm/string.hpp>

#include "../Common/Common.h"
#include "../Common/Connection.h"
#include "../Common/Query.h"
#include "../Common/GameClient.h"
#include "../Common/LoginEncryption.h"
#include "../Common/Functions.h"
#include "../Common/Configuration.h"
#include "../Common/BufferPool.h"
#include "../Common/MysqlDatabase.h"
#include "../Common/MysqlQuery.h"
#include "../Common/Packet.h"

class UniverseConnection : public Connection
{
	void onRead(const boost::system::error_code& e, size_t bytesTransferred);
	void onWrite(const boost::system::error_code& e);

	static uint32 connectionCount;
	
	GameClient gameClient;
	uint32 m_connectionID;

public:
	UniverseConnection(boost::asio::io_service& IOService, BufferPool* hp);
	~UniverseConnection();

	void start();
};

#endif