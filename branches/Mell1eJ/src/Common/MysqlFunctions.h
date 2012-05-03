/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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

#ifndef MYSQLFUNCTIONS_H
#define MYSQLFUNCTIONS_H

#include "Common.h"

#include <iostream>
#include <fstream>
#include <string>
#include <vector>

#include <boost/program_options.hpp>
#include <boost/thread.hpp>

#include "MysqlQuery.h"
#include "Singleton.h"
#include "Structs.h"
#include "CharacterInfo.h"

using namespace std;

namespace MySQLFunctions
{
	bool CheckLogin(string username, string password);
	int32 GetAccountID(string username);
	bool IsAccountBanned(uint32 nClientInst);
	int32 GetNewCharacterId();

	int32 GetAccountCookie(uint32 nClientInst);
	bool SetAccountCookie(uint32 nClientInst, uint32 cookie);

	bool UpdateLastInfo(uint32 nClientInst, string ipAddress);

	bool GetRealm(uint32 realmID, RealmInfo *realm);
	bool GetRealms(vector<RealmInfo> *realms);
	bool GetCharacter(uint32 characterID, CharacterInfo *character);
	bool GetCharacters(uint32 accountID, vector<CharacterInfo> *characters);

	bool DeleteCharacter(uint32 characterID);
};

#endif