/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009 - 2012 The Project Faolan Team

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

#ifndef PLAYERPACKETS_H
#define PLAYERPACKETS_H

#include "../playerAgent.h"

namespace playerPackets
{
	void initAuth(Packet* packet, GameClient* client);
	void sendSmallCharList(GameClient* client);
	void sendCharacterList(GameClient* client);
	void sendRealmList(GameClient* client);
	void sendAgentServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst);
	void sendCSServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst);
	void sendWorldServerAddress(GameClient* client, uint32 ip, uint16 port, uint32 accID,uint32 nClientInst);

	//functions
	uint32 getCharCount(vector<CharacterInfo> characters);
}

#endif