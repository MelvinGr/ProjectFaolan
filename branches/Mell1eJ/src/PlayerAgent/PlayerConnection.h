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

#ifndef PLAYERCONNECTION_H
#define PLAYERCONNECTION_H

#include <iostream>
#include <vector>

#include <boost/enable_shared_from_this.hpp>
#include <boost/bind.hpp>
#include <boost/algorithm/string.hpp>

#include "../Common/Common.h"
#include "../Common/Connection.h"
#include "../Common/GameClient.h"
#include "../Common/LoginEncryption.h"
#include "../Common/Functions.h"
#include "../Common/Configuration.h"
#include "../Common/BufferPool.h"
#include "../Common/PacketBuffer.h"
#include "../Common/Packet.h"
#include "../Common/CharacterInfo.h"
#include "../Common/Structs.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

class PlayerConnection : public Connection
{
	void handlePacket(PacketBuffer *packetBuffer, Packet* packet);
	
	GameClient gameClient;
	CharacterInfo characterInfo;
	
	void SendRealmList();
	void SendCharacterList();

public:
	PlayerConnection(boost::asio::io_service& IOService, BufferPool* hp);
	~PlayerConnection();

	void start();
};

#endif