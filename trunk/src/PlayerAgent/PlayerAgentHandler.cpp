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

#include "PlayerAgent.h"
#include "Packets\playerPackets.h"

void PlayerAgent::PlayerAgentHandler(Packet* packet, GameClient* client)
{
	Log.Debug("Receive opcode: 0x%08x\n", packet->opcode);
	switch(packet->opcode)
	{
	case OPCODES::REQUEST_AUTH:
		{
			Log.Debug("Recv: Auth Init\n");
			playerPackets::initAuth(packet, client);
			Database.deleteEmptyChar(client->charInfo.accountID);
			Database.insertEmptyChar(client);
			break;
		}
	case 0x20c1:
	case OPCODES::REQUEST_SMALLCHARLIST:  //0xDAD1D206:
		{
			Log.Warning("Receive:\n%s\n\n", String::arrayToHexString(packet->packetBuffer->buffer, packet->packetBuffer->bufferLength).c_str());
			string unk1 = packet->data->read<string>();
			Log.Notice("Receive unknown String: %s\n", unk1.c_str());
			//send before charlist char-specific packets
			playerPackets::sendSmallCharList(client);
			break;
		}

	case 0x208e:
	case OPCODES::REQUEST_CHAR_REALMDATA: //0xA3E1FE0D:
		{
			Log.Warning("Receive:\n%s\n\n", String::arrayToHexString(packet->packetBuffer->buffer, packet->packetBuffer->bufferLength).c_str());
			playerPackets::sendCharacterList(client);
			playerPackets::sendRealmList(client);

			uint8 headerData[] = { 0xcb, 0xc6, 0xfc, 0x04 };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
			PacketBuffer aBuffer(500);
			aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x208c);
			aBuffer.write<uint32>(1);
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x20a6:
		{
			uint8 headerData[] = { 0x99, 0x95, 0x92, 0x05 };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x02 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
			PacketBuffer aBuffer(500);
			aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x209c);
			aBuffer.write<uint16>(0);
			aBuffer.write<uint8>(0);
			aBuffer.doItAll(client->clientSocket);

			break;
		}

	case 0x20d6:
	case 0xDD9EC209:
		{
			uint32 languageID = packet->data->read<uint32>();
			Log.Warning("Maybe report language\nLanguage: %i\n", languageID);

			break;
		}
	case 0x20b6:
	case 0xDD85FB0E: // ENTER WORLD
		{
			Log.Warning("Enter World\n");
			client->nClientInst = packet->data->read<uint32>();
			client->charInfo.realmID = (client->nClientInst >> 24) &0x000000ff;
			client->charInfo.characterID = client->nClientInst & 0x00ffffff;

			uint8 headerData[] = { 0x8b, 0xd8, 0x99, 0x02 };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
			PacketBuffer aBuffer(500);
			aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x20b9);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<uint32>(0);
			aBuffer.doItAll(client->clientSocket);

			break;
		}
	case 0x20de:
	case 0x9CB1D10C: //CREATE NEW CHAR
		{
			Log.Warning("Create new Character\n");
			uint32 i_nDimID = packet->data->read<uint32>();

			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == i_nDimID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}

			if(realm == 0) // send error opcode, whatever
			{
				Log.Error("No Realm found\n");
				break;
			}

			
			client->nClientInst = client->charInfo.characterID + (i_nDimID * 0x01000000);
			client->charInfo.realmID = i_nDimID;

			if(client->charInfo.characterID == -1) // send error opcode, whatever
			{
				Log.Error("Error creating characterID\n");
				break;
			}

			uint8 headerData[] = { 0x8b, 0xd8, 0x99, 0x02 };
			uint8 sender[] = { 0x0d, 0x84, 0x04, 0xf2, 0x82, 0x10, 0x03 };
			uint8 receiver[] = { 0x0d, 0x38, 0x57, 0x15, 0x7d, 0x10, 0xeb, 0x8e, 0x95, 0xbf, 0x05 };
			PacketBuffer aBuffer(500);
			aBuffer.setHeader(sender, sizeof(sender), receiver, sizeof(receiver), headerData, sizeof(headerData), 0x20b9);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<uint32>(0);
			aBuffer.doItAll(client->clientSocket);

			break;
		}
	case 0x20ec:
	case 0xA4F2E303:
		{
			Log.Debug("Request for Server Addresses\n");
			RealmInfo* realm = 0;
			for(uint32 i = 0; i < Settings.realms.size(); i++)
			{
				if(Settings.realms[i]->realmID == client->charInfo.realmID)
				{
					realm = Settings.realms[i];
					break; // break forloop
				}
			}

			if(realm == 0) // send error opcode, whatever
			{
				Log.Error("No Realm found\n");
				break;
			}

			Log.Debug("AccID: 0x%08x\n", client->charInfo.accountID);

			playerPackets::sendAgentServerAddress(client, htonl(inet_addr(realm->agentServerIPAddress.c_str())), realm->agentServerPort, client->charInfo.accountID, client->nClientInst);
			playerPackets::sendCSServerAddress(client, htonl(inet_addr(realm->csPlayerAgentIPAddress.c_str())), realm->csPlayerAgentPort, client->charInfo.accountID, client->nClientInst);
			playerPackets::sendWorldServerAddress(client, htonl(inet_addr(realm->worldServerIPAddress.c_str())), realm->worldServerPort, client->charInfo.accountID, client->nClientInst);

			break;
		}
	case 0x209d:
	case 0x86979E0C: //DELETE CHAR
		{
			uint32 charID = packet->data->read<uint32>();
			Log.Warning("Delete Character\nChar-ID: 0x%08x\n", charID);
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