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

#ifndef SENDPACKETS_H
#define SENDPACKETS_H

namespace sendPackets
{
	namespace passBlob
	{
		void CharStats(GameClient* client, uint32 target, uint32* value);

		//Object Handler
		namespace objHandle
		{
			void initObject(GlobalTable* GTable);
			bool checkAlreadySpawned(vector<uint32>* spawned, uint32 objId);
			bool spawnObj(Object tmpObj, GlobalTable* GTable);
		}
	}
	void GI_AckAuthentication(GameClient* client, uint32 value); //0x01
	void GI_PingPong(Packet* packet, GameClient* client);
	void GI_ReportDimensionID(GameClient* client, string realmId); //0x0c
	void GI_ReportServerID(GameClient* client, uint32 value); //0x0d


	void GCI_PingPong(Packet* packet, GameClient* client);

	namespace other
	{
		void removeEffect(GlobalTable* GTable, uint32 effectId, uint32 npcId);
	}
}

#endif