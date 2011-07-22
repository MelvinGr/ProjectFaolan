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

#include "WorldServer.h"

void WorldServer::ItemHandler::sendCharItems(GameClient* client)
{
	if(Database.getCharItems(client->charInfo.characterID, &client->charItems))
	{
		if(client->charItems.size() > 0)
		{
			for(uint32 c = 0; c < client->charItems.size(); c++)
			{
				setItemStats(client, client->charItems[c].pos, client->charItems[c].id);
			}
		}
	}
	else
	{
		Log.Error("Error receiving Character Items for char with id %i\n", client->charInfo.characterID);
	}
}

void WorldServer::ItemHandler::setItemStats(GameClient* client, uint32 placeId, uint32 itemId)
{
	PacketBuffer aBuffer(50000);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, Opcodes::GCI_addItem);
	aBuffer.write<uint32>(0x00000065);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(placeId);
	aBuffer.write<uint32>(0x00000017); 
	aBuffer.write<uint32>(itemId);
	aBuffer.doItAll(client->clientSocket);
}

void WorldServer::ItemHandler::setItemData(GlobalTable* GTable, Item* tmpItem)
{
	if(GTable->ITEMS.size()>0)
	{
		for(uint32 c = 0; c < GTable->ITEMS.size(); c++)
		{
			if(GTable->ITEMS[c].id == tmpItem->id)
			{
				tmpItem->data0 = GTable->ITEMS[c].data0;
				tmpItem->data1 = GTable->ITEMS[c].data1;
				tmpItem->data2 = GTable->ITEMS[c].data2;
				tmpItem->data3 = GTable->ITEMS[c].data3;
				tmpItem->data4 = GTable->ITEMS[c].data4;
				tmpItem->iLevel = GTable->ITEMS[c].iLevel;
				return;
			}
		}
		Log.Error("No itemdata for item with id 0x%08x\n", tmpItem->id);
	}
}