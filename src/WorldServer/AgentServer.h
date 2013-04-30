/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2012, 2013 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef AGENTSERVER_H
#define AGENTSERVER_H

#include "../Common/Common.h"
#include "../Common/Settings.h"
#include "../Common/MysqlDatabase.h"
#include "../Common/Functions.h"
#include "../Common/PacketBuffer.h"
#include "../Common/Packet.h"
#include "../Common/Networking.h"
#include "../Common/Logger.h"
#include "../Common/Client.h"

using namespace std;

namespace AgentServer
{
	void AgentServerHandler(GameClient* client, vector<GameClient*>* clientList);

	void HandleClient(void* socket);

	//Messages
	void sendNewChannel(GameClient* client, uint8 typ, uint32 channelId, string channelName);
}
#endif