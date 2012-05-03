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

#ifndef MYSQLDATABASE_H
#define MYSQLDATABASE_H

#include "Common.h"

#ifdef WINDOWS
	#include <Winsock2.h>
#else
	#include <sys/types.h>
	#include <sys/socket.h>
	#include <errno.h>
#endif

#include <vector>
#include <iostream>
#include <stdio.h>
#include <mysql/mysql.h>

#include "Structs.h"
#include "Logger.h"
#include "Client.h"

#define Database MySQLDatabase::getSingleton()
class MySQLDatabase : public Singleton<MySQLDatabase>
{
private:
	MYSQL mysql;

public:
    bool initMysql(string host, int32 port, string login, string password, string database);
    void closeMysql();

	//Realms from db
	void loadRealms(vector<RealmInfo*>* realms);
	//

	//ServerStart
	bool loadGlobalSpells(vector<Spells>* SPELLS);
	bool loadGlobalNpcs(vector<NPC>* NPCS);
	bool loadGlobalObjects(vector<Object>* OBJECTS);
	bool loadGlobalItems(vector<Item>* ITEMS);
	//-----------

    bool checkLogin(string username, string password);
    bool updateLastInfo(uint32 accountID, string ip);

    bool getBannedState(uint32 accountID);
    int32 getAccountID(string username);
    int32 getAccountCookie(uint32 accountID);
	int32 generateCharId();
	uint32 getEmptyChar_Id(uint32 accountID);
	bool updateCharacter(CharacterInfo* charInfo);
	bool isValidCharName(string charname);
	bool insertEmptyChar(GameClient* client);

    bool setAccountCookie(string username, uint32 cookie);
    bool setAccountCookie(uint32 accountID, uint32 cookie);
	bool getAccountTrail(uint32 accountID);

	int32 getAccountByCharacter(CharacterInfo character);

    int32 getCharacterCount(uint32 accountID);
    bool getCharacters(uint32 accountID, vector<CharacterInfo>* characterInfos);
    bool getCharacterInfo(uint32 characterID, CharacterInfo* info);

	string getLevelAbilitys(uint32 classId, uint32 level);
	string getNewLevelAbilitys(uint32 classId, uint32 level);
	string getLevelSpells(uint32 classId, uint32 level);
	string getNewLevelSpells(uint32 classId, uint32 level);
	bool getAbilityData(uint32 abilityId, Item* ability);

	bool insertItemEntry(uint32 characterID);
	bool checkItemTable(uint32 characterID);
	bool getItemData(string item_name, Item* item);
	bool getItemData(uint32 itemId, Item* item);
	bool getNpcItems(uint32 npcId, vector<Item>* item);

    bool deleteCharacter(uint32 characterID);
	bool deleteCharacterData(uint32 characterID);
	bool deleteEmptyChar(uint32 accountID);
	string getCharacterName(uint32 characterID);
    int32 getNewCharacterID();

	bool getNpcs(uint32 mapId, vector<NPC>* npcs);
	bool getNpcDetail(uint32 npcID, NPC* spawn);
	bool getNpcMesh(uint32 npcID, NPC* spawn);
	uint32 getNpcFraction(uint32 npcId);

	bool getMaxStats(GameClient* clientInfo);

	bool getMapDetails(string map_name, teleport* info);

	//Character
	bool getCharItems(uint32 charId, vector<Item>* item);
	bool getCharBar(uint32 charId, vector<Item>* barItems);
};

#endif
