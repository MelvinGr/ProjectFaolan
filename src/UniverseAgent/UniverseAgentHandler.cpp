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

#include "UniverseAgent.h"
#include "Packets/universePackets.h"

void UniverseAgent::UniverseAgentHandler(Packet* packet, GameClient* client)
{
	switch(packet->opcode)
	{
	case 0x2000: // InitiateAuthentication
		{
			Log.Warning("Receive:\n%s\n\n", String::arrayToHexString(packet->packetBuffer->buffer, packet->packetBuffer->bufferLength).c_str());
			universePackets::initAuth(packet, client);
			break;
		}

	case 0x2001: // AnswerChallenge
		{
			string cAnswerChallenge = packet->data->read<string>();

			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);
			Log.Warning("Receive:\n%s\n\n", String::arrayToHexString(packet->packetBuffer->buffer, packet->packetBuffer->bufferLength).c_str());
			vector<string> decryptedDataVector = String::splitString(decryptedData, "|");
			
			if(decryptedDataVector.size() != 3) // wrong decryption
			{
				Log.Error("Wrong decryption of username and password\n");
				universePackets::AckAuthenticate(packet, client, 0xffffff, 0, "", 0, 0, 0x04);
				break;
			}

			string username = decryptedDataVector[0];
			string nChallenge = decryptedDataVector[1];
			string password = decryptedDataVector[2];
			
			if(nChallenge != client->authChallenge) // wrong auth
			{
				Log.Error("Wrong Auth\n");
				universePackets::AckAuthenticate(packet, client, 0xffffff, 0, "", 0, 0,0x04);
				break;
			}
			
			if(!Database.checkLogin(username, password)) // wrong login
			{
				Log.Error("Wrong Login\n");
				universePackets::AckAuthenticate(packet, client, 0xffffff, 0, "", 0, 0, 0x0e);
				break;
			}
			
			client->nClientInst = Database.getAccountID(username);
			
			if(client->nClientInst == -1) // could not get clientInst
			{
				Log.Error("Could not get clientInst\n");
				universePackets::AckAuthenticate(packet, client, 0xffffff, 0, "", 0, 0, 0x04);
				break;
			}

			if(Database.getBannedState(client->nClientInst)) // player banned
			{
				Log.Error("Banned Player try to login\n");
				universePackets::AckAuthenticate(packet, client, 0xffffff, 0, "", 0, 0, 0x17);
				break;
			}

			Log.Debug("User %s is logging on with acc-ID: 0x%08x\n", username.c_str(), client->nClientInst);

			universePackets::setRegionState(packet, client);

			Database.deleteEmptyChar(client->charInfo.accountID);

			uint32 cookie = Database.generateCharId();
			client->charInfo.characterID = cookie;
			client->charInfo.accountID = client->nClientInst;
			printf("Accountcookie: 0x%08x\nClientInst: 0x%08x\n", cookie, client->nClientInst);
			//Database.insertEmptyChar(client);
			//uint32 cookie = Network::generateCookie();
			Database.setAccountCookie(client->nClientInst, cookie);
			Database.updateLastInfo(client->nClientInst, client->ipAddress);

			string playerAgentIPAddress = Settings.playerAgentIPAddress + ":" +
				String::toString(Settings.playerAgentPort);

			uint64 accID = 0x0000271200000000 + client->nClientInst;
			//accID = 0x0000271257e5476b;
			universePackets::AckAuthenticate(packet, client, 1, accID, playerAgentIPAddress, cookie, 0, 0);

			break;
		}

	default:
		{
			//Log.Warning("Unknown Packet With Opcode: 0x%08X\n", packet->opcode);
			//Log.Warning("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());
			Log.Error("Receive unknown packet:\n%s\n\n", String::arrayToHexString(packet->packetBuffer->buffer, packet->packetBuffer->bufferLength).c_str());

			break;
		}
	}
}
