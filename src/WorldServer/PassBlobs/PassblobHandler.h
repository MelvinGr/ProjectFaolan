/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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

#ifndef PASSBLOB_H
#define PASSBLOB_H

namespace PassBlob
{
	void PassBlobHandler(Packet* packet, GlobalTable* GTable); //GameClient* client);
	
	//Movement
	void CharMovement(Packet* packet, GameClient* client);

	enum Movement
	{
		MOVE_WALK		= 0x1a,
		MOVE_ROTATECAM	= 0x1e,
		MOVE_JUMP		= 0x12,
		MOVE_RUN		= 0x3a,
		MOVE_RUNJUMP	= 0x32,
		MOVE_SWIM		= 0x0a
	};

	//PlayerAction
	void CharAction(Packet* packet, GlobalTable* GTable); //GameClient* client);
	
	enum ActionLength
	{
		ACTION_SpeakWithNPC		= 0x4e,
		ACTION_SpellAtt			= 0x60,
		ACTION_NormalAtt		= 0x66,
		ACTION_WearUnwearItem	= 0x8e
	};

	//Select Object
	void SelectObject(Packet* packet, GameClient* client);

	enum SelectTyp
	{
		SELECT_NPC = 0x21
	};

	//DeleteItem
	void DeleteItem(Packet* packet, GameClient* client);

	//LoadBarFull
	void enterWorld(Packet* packet, GameClient* client);
	void firstEnterWorld(Packet* packet, GameClient* client);
	void enterCharCreation(Packet* packet, GameClient* client);
}

#endif