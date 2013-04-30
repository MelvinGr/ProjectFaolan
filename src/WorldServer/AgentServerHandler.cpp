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

#include "AgentServer.h"

void AgentServer::AgentServerHandler(GameClient* client, vector<GameClient*>* clientList)
{
	if(!client->Receive(false))
	{
		Log.Error("Error receiving Packet\n");
		return;
	}
	Log.Warning("AgentNet: Packet:\n%s\n\n", String::arrayToHexString(client->receiveBuffer.buffer, client->receiveBuffer.bufferLength).c_str());
	uint16 opcode = client->receiveBuffer.read<uint16>();
	uint16 length = client->receiveBuffer.read<uint16>();
	/*
	00000000  00 00 00 15 00 00 00 01  01 14 90 4d 15 6e 68 a1 ........ ...M.nh.
	00000010  00 07 45 6e 67 6c 69 73  68                      ..Englis h
	*/
	//Log.Notice("AgentNet: [%s][%s] Received Opcode: 0x%04X\n", String::timeString().c_str(), client->ipAddress.c_str(), opcode);
	//Log.Notice("%s\n\n", String::arrayToHexString(client->receiveBuffer.buffer, client->receiveBuffer.bufferLength).c_str());
	switch(opcode)
	{
	case 0x0000:
		{
			uint32 unk0 = client->receiveBuffer.read<uint32>();
			client->nClientInst = client->receiveBuffer.read<uint32>();
			client->charInfo.accountID = client->receiveBuffer.read<uint32>();
			client->charInfo.characterID = client->nClientInst & 0x00ffffff;
			string language = client->receiveBuffer.read<string>();

			Log.Debug("AgentNet: Send Chat-Channel-List\n");
			sendNewChannel(client, 0x04, 0x00002328, "~Global");
			sendNewChannel(client, 0x90, 0x00000fa0, "~~Playfield");

			break;
		}
	default:
		{
			Log.Warning("AgentNet: Unknown Packet With Opcode: 0x%04X Data:\n", opcode);
			Log.Warning("AgentNet:\n%s\n\n", String::arrayToHexString(client->receiveBuffer.buffer, client->receiveBuffer.bufferLength).c_str());

			break;
		}
	}
}