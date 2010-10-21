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
	client->Receive(false);

	uint16 opcode = client->receiveBuffer.read<uint16>();
	uint16 length = client->receiveBuffer.read<uint16>(); // opcode & length dont count in length

	Log.Notice("[%s][%s] Received Opcode: 0x%04X\n", String::timeString().c_str(), client->ipAddress.c_str(), opcode);
	//Log.Notice("%s\n\n", String::arrayToHexString(client->receiveBuffer.buffer, client->receiveBuffer.bufferLength).c_str());

	switch(opcode)
	{
	case 0x0000:
		{
			uint32 unk0 = client->receiveBuffer.read<uint32>();
			client->nClientInst = client->receiveBuffer.read<uint32>();
			client->charInfo.accountID = client->receiveBuffer.read<uint32>();
			client->charInfo.characterID = client->nClientInst & 0x00ffffff;

			PacketBuffer aBuffer(500);
			aBuffer.write<uint16>(0x003c); // opcode
			aBuffer.write<uint16>(0x0013); // length
			aBuffer.write<uint8>(0x04);
			aBuffer.write<uint32>(0x0000232a);
			aBuffer.write<string>("~Trial");
			aBuffer.write<uint32>(0x00008044);
			aBuffer.write<uint16>(0x0000);
			aBuffer.doItAllAgentServer(client->clientSocket);

			aBuffer = PacketBuffer(500);			
			aBuffer.write<uint32>(0x00050000);
			aBuffer.doItAllAgentServer(client->clientSocket);

			//string char_name = "Necroonan";
			uint16 size = 0;
			string char_name = Database.getCharacterName(client->charInfo.characterID);
			if(char_name.length() > 0)
			{
				size = 4+4+2+char_name.length();
				aBuffer = PacketBuffer(500);
				aBuffer.write<uint16>(0x0014);
				aBuffer.write<uint16>(size);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<string>(char_name);
				aBuffer.doItAllAgentServer(client->clientSocket);
			}
			else
			{
				//string character = "Character" + String::toString(client->charInfo.characterID); // Character285324741
				string character = "Character" + String::toString(client->nClientInst); // Character285324741
				size = character.size() + 2 + (2 * 4);
				aBuffer = PacketBuffer(500);
				aBuffer.write<uint16>(0x0014);
				aBuffer.write<uint16>(size); //0x001c
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<string>(character);
				aBuffer.doItAllAgentServer(client->clientSocket);
			}
			//*/
			string welcome = "Welcome to Project Faolan Server!";
			size = welcome.size() + 2 + (2 * 2);
			aBuffer = PacketBuffer(500);
			aBuffer.write<uint16>(0x0023);
			aBuffer.write<uint16>(size); //0x0038
			aBuffer.write<uint16>(0x0000);
			//aBuffer.write<string>("<localized category=20000 token=\"welcome_message\">");
			//aBuffer.write<string>("Welcome to Project Faolan!\n" + String::serverInfoStr());
			aBuffer.write<string>(welcome);
			aBuffer.write<uint16>(0x0000);
			aBuffer.doItAllAgentServer(client->clientSocket);

			aBuffer = PacketBuffer(500);
			aBuffer.write<uint16>(0x003c);
			aBuffer.write<uint16>(0x0013);
			aBuffer.write<uint8>(0x04);
			aBuffer.write<uint32>(0x0000232a);
			aBuffer.write<string>("~Trial");
			aBuffer.write<uint32>(0x00008044);
			aBuffer.write<uint16>(0x0000);
			aBuffer.doItAllAgentServer(client->clientSocket);

			break;
		}

	case 0x0041: // global message
		{
			uint8 unk0 = client->receiveBuffer.read<uint8>(); // 04
			uint32 unk1 = client->receiveBuffer.read<uint32>();
			string text = client->receiveBuffer.read<string>();
			uint16 unk2 = client->receiveBuffer.read<uint16>(); // 00 00

			string char_name = Database.getCharacterName(client->charInfo.characterID);
			PacketBuffer bBuffer(500);
			bBuffer.write<uint16>(0x0014);
			bBuffer.write<uint16>(0x001c);
			bBuffer.write<uint32>(client->nClientInst);
			bBuffer.write<uint32>(0x00000000);
			bBuffer.write<string>(char_name);

			PacketBuffer aBuffer(500);
			aBuffer.write<uint16>(0x0041); //opcode
			aBuffer.write<uint16>(0x00); // length (which now get overwritten)

			aBuffer.write<uint8>(unk0);
			aBuffer.write<uint32>(0x00002328);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<string>(text);
			aBuffer.write<uint16>(unk2);

			aBuffer.finalizeAgentServer();
			bBuffer.finalizeAgentServer();

			for(uint32 i = 0; i < clientList->size(); i++)
			{
				if(client->nClientInst != clientList->at(i)->nClientInst)
				{
					send(clientList->at(i)->clientSocket, (int8*)bBuffer.buffer, bBuffer.bufferLength, 0);
				}
				send(clientList->at(i)->clientSocket, (int8*)aBuffer.buffer, aBuffer.bufferLength, 0);
			}

			aBuffer.drop();
			bBuffer.drop();

			break;
		}
	case 0x0064:
		{
			uint16 unk0 = client->receiveBuffer.read<uint16>();
			uint8 unk1 = client->receiveBuffer.read<uint8>();

			PacketBuffer aBuffer(500);
			aBuffer.write<uint16>(0x0064);
			aBuffer.write<uint16>(0x0003);
			aBuffer.write<uint16>(unk0);
			aBuffer.write<uint8>(unk1);
			aBuffer.doItAllAgentServer(client->clientSocket);

			break;
		}
	case 0x9f9f:
		{
			uint32 intern_opcode = client->receiveBuffer.read<uint32>();
			uint32 nClientInst = client->receiveBuffer.read<uint32>();
			string text = client->receiveBuffer.read<string>();
			//
			PacketBuffer aBuffer(500);
			aBuffer.write<uint16>(0x0022); //opcode
			aBuffer.write<uint16>(0x00); // length (which now get overwritten)
			aBuffer.write<uint32>(nClientInst);
			aBuffer.write<string>(text);
			aBuffer.write<uint16>(0x0001);
			aBuffer.write<uint8>(0);

			aBuffer.finalizeAgentServer();

			for(uint32 i = 0; i < clientList->size(); i++)
			{
				send(clientList->at(i)->clientSocket, (int8*)aBuffer.buffer, aBuffer.bufferLength, 0);
			}
			aBuffer.drop();

			break;
		}
	default:
		{
			Log.Warning("Unknown Packet With Opcode: 0x%04X Data:\n", opcode);
			Log.Warning("%s\n\n", String::arrayToHexString(client->receiveBuffer.buffer, client->receiveBuffer.bufferLength).c_str());

			break;
		}
	}
}