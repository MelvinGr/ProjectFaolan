/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful, 
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef GAMECLIENT_H
#define GAMECLIENT_H

#include "Common.h"

#include <string>

#include "CharacterInfo.h"

using namespace std;

class GameClient
{
public:
	string ipAddress;

	string authChallenge;
	string username;

	uint32 nClientInst;
	uint32 nCookie;

	//CharacterInfo characterInfo;

	GameClient();
	~GameClient();
};

#endif