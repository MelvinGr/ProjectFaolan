/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

#include "../WorldServer.h"

void sendPackets::GI_ReportDimensionID(GameClient* client, string realmId)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_ReportDimesionID); // ReportDimesionID
	aBuffer.write<string>(realmId); // dimensionID
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_ReportDimensionID\n");
}

void sendPackets::GI_ReportServerID(GameClient* client, uint32 value)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GI_ReportServerID); // ReportServerID
	aBuffer.write<uint32>(gameUnknown1);
	aBuffer.write<uint32>(value); // serverid? 
	aBuffer.doItAll(client->clientSocket);

	Log.Debug("Sent GI_ReportServerID\n");
}