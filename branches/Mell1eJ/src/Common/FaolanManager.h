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

#ifndef ManagerHeader_H
#define ManagerHeader_H

#include <iostream>

#include <boost/enable_shared_from_this.hpp>
#include <boost/bind.hpp>
#include <boost/algorithm/string.hpp>

#include "Common.h"
#include "Connection.h"
#include "GameClient.h"
#include "Functions.h"
#include "Configuration.h"
#include "BufferPool.h"
#include "PacketBuffer.h"
#include "Packet.h"

enum FaolanManagerOpcodes : uint16
{
	RequestRealmInfo,
	RequestRealmInfoResponse,
	RequestRegister, 
	RequestRegisterResponse,
	ServerStatusChange,
	ServerStatusChangeResponse,
	Ping,
	Pong
};

enum FaolanManagerSenderReceivers : uint8
{
	FaolanManagerID,
	UniverseAgentID,
	PlayerAgentID,
	WorldServerID,
	AgentServerID,
	CSPlayerAgentID
};

static const int8* FaolanManagerSenderReceiversText[] = 
{ "FaolanManager", "UniverseAgent", "PlayerAgent", "WorldServer", "AgentServer", "CSPlayerAgent" };

namespace FaolanManager
{
	// Follow UniverseAgent structure
	FAOLANEXPORTED void WriteManagerHeader(PacketBuffer &packetBuffer, FaolanManagerSenderReceivers sender, 
		FaolanManagerSenderReceivers receiver, FaolanManagerOpcodes opcode);
}

#endif