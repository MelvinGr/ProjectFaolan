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

#ifndef MysqlFunctions_H
#define MysqlFunctions_H

#include "Common.h"

#include <iostream>
#include <string>
#include <vector>

#include "MysqlDatabase.h"
#include "Structs.h"

namespace MysqlFunctions
{
	FAOLANEXPORTED bool CheckLogin(const std::string& username, const std::string& password);
	FAOLANEXPORTED int32 GetAccountID(const std::string& username);
	FAOLANEXPORTED bool IsAccountBanned(uint32 nClientInst);
	FAOLANEXPORTED int32 GetNewCharacterId();

	FAOLANEXPORTED int32 GetAccountCookie(uint32 nClientInst);
	FAOLANEXPORTED bool SetAccountCookie(uint32 nClientInst, uint32 cookie);

	//bool SetCSPlayerAgentOnline(uint32 realmID, uint32 status);
	//bool SetAgentServerOnline(uint32 realmID, uint32 status);
	//bool SetWorldServerOnline(uint32 realmID, uint32 status);

	FAOLANEXPORTED bool UpdateLastInfo(uint32 nClientInst, const std::string& ipAddress);

	FAOLANEXPORTED bool GetRealm(uint32 realmID, RealmInfo &realm);
	FAOLANEXPORTED bool GetRealms(std::vector<RealmInfo> &realms);
	FAOLANEXPORTED bool GetCharacter(uint32 characterID, CharacterInfo &character);
	FAOLANEXPORTED bool GetCharacters(uint32 accountID, std::vector<CharacterInfo> &characters);

	FAOLANEXPORTED bool DeleteCharacter(uint32 characterID);
};

#endif