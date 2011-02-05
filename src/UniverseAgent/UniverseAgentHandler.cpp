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

#include "UniverseAgent.h"

void UniverseAgent::UniverseAgentHandler(Packet* packet, GameClient* client)
{
	switch(packet->opcode)
	{
	case 0x00: // InitiateAuthentication
		{
			string cDevName = packet->data->read<string>();
			string cUserName = packet->data->read<string>();
			uint32 nGameID = packet->data->read<uint32>();

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x00); // Challenge
			aBuffer.write<string>(client->authChallenge);

			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x01: // AnswerChallenge
		{
			string cAnswerChallenge = packet->data->read<string>();

			string decryptedData = LoginEncryption::decryptLoginKey(cAnswerChallenge);
			vector<string> decryptedDataVector = String::splitString(decryptedData, "|");

			if(decryptedDataVector.size() != 3) // wrong decryption
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x02); // LoginProblem
				aBuffer.write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				aBuffer.doItAll(client->clientSocket);

				break;
			}

			string username = decryptedDataVector[0];
			string nChallenge = decryptedDataVector[1];
			string password = decryptedDataVector[2];

			if(nChallenge != client->authChallenge) // wrong auth
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x02); // LoginProblem
				aBuffer.write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				aBuffer.doItAll(client->clientSocket);

				break;
			}

			if(!Database.checkLogin(username, password)) // wrong login
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x01); // AckAuthenticate
				aBuffer.write<uint32>(0x00); // nAuthStatus
				aBuffer.write<uint64>(0x00); // cPlayerID
				aBuffer.write<uint32>(0x00); // uCookie
				aBuffer.write<uint32>(0x00); // eReason
				aBuffer.write<uint16>(0x0E); // error code: invalid username/password
				aBuffer.doItAll(client->clientSocket);

				break;
			}

			client->nClientInst = Database.getAccountID(username);
			if(client->nClientInst == -1) // could not get clientInst
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x02); // LoginProblem
				aBuffer.write<uint32>(0x04); // eLoginStatus -- error code: internal server error
				aBuffer.doItAll(client->clientSocket);

				break;
			}

			if(Database.getBannedState(client->nClientInst)) // player banned
			{
				PacketBuffer aBuffer(500);
				aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x01);
				aBuffer.write<uint32>(0x00); // nAuthStatus
				aBuffer.write<uint64>(0x00); // cPlayerID
				aBuffer.write<uint32>(0x00); // uCookie
				aBuffer.write<uint32>(0x00); // eReason
				aBuffer.write<uint16>(0x17); // error code: account freezed
				aBuffer.doItAll(client->clientSocket);

				break;
			}

			uint8 real[] =
			{
				0x00, 0x00, 0x06, 0x08,
				0x00, 0x00, 0x00, 0x02,
				0x00, 0x00, 0x00, 0x00,
				0x01, 0x01, 0x01, 0x01,
				0x3f, 0x80,
				0x00, 0x00,
				0x3f, 0x80,
				0x00, 0x00, 0x01, 0x01,
				0x00, 0x00, 0x00, 0x00,
				0x09
			};

			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x05); // SetRegionState
			aBuffer.writeArray(real, sizeof(real));
			aBuffer.doItAll(client->clientSocket);

			Database.deleteEmptyChar(client->charInfo.accountID);
			uint32 cookie = Database.generateCharId();
			client->charInfo.characterID = cookie;
			client->charInfo.accountID = client->nClientInst;
			printf("Accountcookie: 0x%08x\n", cookie);
			//Database.insertEmptyChar(client);
			//uint32 cookie = Network::generateCookie();
			Database.setAccountCookie(client->nClientInst, cookie);
			Database.updateLastInfo(client->nClientInst, client->ipAddress);

			string playerAgentIPAddress = Settings.playerAgentIPAddress + ":" +
				String::toString(Settings.playerAgentPort);

			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("UniverseAgent", "UniverseInterface", 1, 0, client->nClientInst, 0, 0x01); // AckAuthenticate
			aBuffer.write<uint32>(0x01); // nAuthStatus
			aBuffer.write<uint64>(client->nClientInst); // cPlayerID
			aBuffer.write<string>(playerAgentIPAddress); // cTerritoryManagerAddr
			aBuffer.write<uint32>(cookie); // uCookie
			aBuffer.write<uint32>(0x00); // eReason
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x02: // ClientDisconnected
		{
			uint32 connectionID = packet->data->read<uint32>(); // int64 ? - InstanceType

			closesocket(client->clientSocket);
			break;
		}

	default:
		{
			Log.Warning("Unknown Packet With Opcode: 0x%08X\n", packet->opcode);
			Log.Warning("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());

			break;
		}
	}
}
