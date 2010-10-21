/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

#ifndef PLAYERAGENT_H
#define PLAYERAGENT_H

#include <time.h>
#include "../Common/Common.h"
#include "../Common/Settings.h"
#include "../Common/MysqlDatabase.h"
#include "../Common/Functions.h"
#include "../Common/PacketBuffer.h"
#include "../Common/Packet.h"
#include "../Common/Networking.h"
#include "../Common/Logger.h"

using namespace std;

namespace PlayerAgent
{
	void sendCharacterList(GameClient* client);
	void sendRealmList(GameClient* client);

	void PlayerAgentHandler(Packet* packet, GameClient* client);

	void HandleClient(void* socket);
}
#endif