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

#include "AgentServer.h"

void AgentServer::sendNewChannel(GameClient* client, uint8 typ, uint32 channelId, string channelName)
{
	PacketBuffer aBuffer(500);
	aBuffer.write<uint16>(0x003c); //opcode for new cahnnel
	aBuffer.write<uint16>(0);
	aBuffer.write<uint8>(typ);
	aBuffer.write<uint32>(channelId);
	aBuffer.write<string>(channelName);
	aBuffer.write<uint32>(0x00008044);
	aBuffer.write<uint16>(0);
	aBuffer.doItAllAgentServer(client->clientSocket);
}