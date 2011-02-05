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

#include "MysqlDatabase.h"

CreateFileSingleton(MySQLDatabase);

bool MySQLDatabase::initMysql(string host, int32 port, string login, string password, string database)
{
	mysql_init(&mysql);

	return mysql_real_connect(&mysql, host.c_str(), login.c_str(), password.c_str(), database.c_str(), port, 0, 0) &&
		(mysql_select_db(&mysql, database.c_str()) == 0);
}

void MySQLDatabase::closeMysql()
{
	mysql_close(&mysql);
}

//Server Start Functions

bool MySQLDatabase::loadGlobalSpells(vector<Spells>* SPELLS)
{
	MYSQL_RES* res;
	MYSQL_ROW row;
	uint32 knownSpells = 0;
	Log.Notice("Loading Spells in table\n|");
	int8 query[1000];
	sprintf(query, "SELECT * FROM spellData");
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			if(atoi(row[0]) > 0)
			{
				knownSpells++;
			}
		}
		mysql_free_result(res);
	}
	else
	{
		Log.Error("ERROR check maxCounter\n");
		return false;
	}

	uint32 tmpCounter = 0;
	uint32 tmpBar = 0;
	uint32 lastValue = 0;
	sprintf(query, "SELECT * FROM spellData");
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			Spells tmpSpell;
			tmpSpell.id = atoi64(row[0]);
			if(tmpSpell.id > 0)
			{
				tmpSpell.level = atoi(row[1]);
				tmpSpell.name = row[2];
				tmpSpell.castTime = atoi64(row[3]);
				tmpSpell.minDmg = atoi64(row[4]);
				tmpSpell.maxDmg = atoi64(row[5]);
				tmpSpell.costMana = atoi64(row[6]);
				tmpSpell.effectSpell = row[7];

				SPELLS->push_back(tmpSpell);
				tmpCounter++;
				tmpBar = tmpCounter / knownSpells;
				if((tmpBar*50) > lastValue)
				{
					uint8 dif= (tmpBar*50) - lastValue;
					for(int v=0; v < dif; v++)
					{
						Log.Notice("=");
					}
					lastValue = tmpBar;
				}
			}
		}
		Log.Notice("|\n%i Spells loaded\n\n", knownSpells);
		mysql_free_result(res);
		return true;
	}
	else
	{
		Log.Error("Error at loading spells in table!!!\n\n");
		return false;
	}
}

bool MySQLDatabase::ladGlobalNpcs(vector<NPC>* NPCS)
{
	MYSQL_RES* res;
	MYSQL_ROW row;
	uint32 knownSpells = 0;
	Log.Notice("Loading NPCs in table\n|");
	int8 query[1000];
	sprintf(query, "SELECT * FROM npcs");
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			if(atoi(row[0]) > 0)
			{
				knownSpells++;
			}
		}
		mysql_free_result(res);
	}
	else
	{
		Log.Error("Error at loading NPCs in table!!!\n\n");
		return false;
	}

	uint32 tmpCounter = 0;
	uint32 tmpBar = 0;
	uint32 lastValue = 0;

	sprintf(query, "SELECT * FROM npcs");
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			NPC npcInfo;
			npcInfo.npcId = atoi(row[0]);
			if(npcInfo.npcId > 0)
			{
				npcInfo.name = row[1];
				npcInfo.nameId = atoi64(row[2]);
				npcInfo.mapId = atoi(row[3]);
				npcInfo.action = atoi(row[4]);
				npcInfo.level = atoi(row[5]);
				npcInfo.fraction = atoi(row[6]);
				npcInfo.health = atoi64(row[7]);
				npcInfo.stamina = atoi64(row[8]);
				npcInfo.mana = atoi64(row[9]);
				npcInfo.getExp = atoi64(row[10]);
				npcInfo.extras = row[11];

				npcInfo.curHealth = npcInfo.health;
				npcInfo.curStamina = npcInfo.stamina;
				npcInfo.curMana = npcInfo.mana;

				getNpcMesh(npcInfo.npcId, &npcInfo);
				
				npcInfo.combat.target = 0;
				npcInfo.move.isMoving = false;
				npcInfo.attackMode = false;
				time(&(npcInfo.combat.lastcheck));
				
				NPCS->push_back(npcInfo);
				tmpCounter++;
				tmpBar = tmpCounter / knownSpells;
				if((tmpBar*50) > lastValue)
				{
					uint8 dif= (tmpBar*50) - lastValue;
					for(int v=0; v < dif; v++)
					{
						Log.Notice("=");
					}
					lastValue = tmpBar;
				}
			}
		}
		Log.Notice("|\n%i NPCs loaded\n\n", knownSpells);
		mysql_free_result(res);
		return true;
	}
	else
	{
		Log.Error("Error at loading NPCs in table!!!\n\n");
		return false;
	}
}

//----------------------

bool MySQLDatabase::checkLogin(string username, string password)
{
	MYSQL_RES* res;

	int8 query[1000];
	sprintf(query,"SELECT * FROM accounts WHERE username = '%s' AND password = '%s'", username.c_str(), password.c_str());
	bool result = (mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && mysql_fetch_row(res);

	mysql_free_result(res);
	return result;
}

bool MySQLDatabase::updateLastInfo(uint32 accountID, string ip)
{
	time_t rawtime;
	time(&rawtime);
	tm* timeinfo = localtime(&rawtime);

	int8 query[1000];
	sprintf(query, "UPDATE accounts SET last_ip = '%s', last_connection = '%02i/%02i/%04i %02i:%02i' WHERE account_id = %u",
		ip.c_str(), timeinfo->tm_mday, timeinfo->tm_mon + 1, timeinfo->tm_year + 1900, timeinfo->tm_hour, timeinfo->tm_min, accountID);

	return (mysql_query(&mysql, query) == 0);
}

bool MySQLDatabase::getBannedState(uint32 accountID)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT state FROM accounts WHERE account_id = %u", accountID);

	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		uint64 result = atoi(row[0]);
		mysql_free_result(res);
		return result > 0;
	}
	else return false;
}

int32 MySQLDatabase::getAccountID(string username)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query,"SELECT account_id FROM accounts WHERE username = '%s'", username.c_str());

	if((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		uint64 result = atoi(row[0]);
		mysql_free_result(res);
		return result;
	}
	else return -1;
}

int32 MySQLDatabase::getAccountCookie(uint32 accountID)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT cookie FROM accounts WHERE account_id = %u", accountID);

	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		uint64 result = atoi(row[0]);
		mysql_free_result(res);
		return result;
	}
	else return -1;
}

int32 MySQLDatabase::generateCharId()
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT MAX(character_id) FROM characters");
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		if(row[0])
		{
			uint64 result = atoi(row[0]);
			mysql_free_result(res);
			return result + 1;
		}
		else return 1;
	}
	else return -1;
}

uint32 MySQLDatabase::getEmptyChar_Id(uint32 accountID)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT character_id FROM characters WHERE account_id = %u AND level=%u", accountID, 0);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		uint64 result = atoi(row[0]);
		mysql_free_result(res);
		return result;
	}
	else return 0;
}

bool MySQLDatabase::updateCharacter(CharacterInfo* charInfo)
{
	deleteCharacter(charInfo->characterID);

	time_t rawtime;
	time(&rawtime);
	tm* timeinfo = localtime(&rawtime);

	int8 query[1000];
	sprintf(query, "INSERT INTO characters VALUES ('%u', '%u', '%s', '%u', '%u', '%u', '%u', '1', '%u', 'en', '%u', '%u', '%u', '%02i/%02i/%04i %02i:%02i', '%u', '%u', '%u', '1126113')",
		charInfo->characterID, charInfo->accountID, charInfo->name.c_str(), charInfo->race, charInfo->Class, charInfo->level, 
		charInfo->sex, charInfo->map, charInfo->headmesh, charInfo->size, charInfo->voice,
		timeinfo->tm_mday, timeinfo->tm_mon + 1, timeinfo->tm_year + 1900, timeinfo->tm_hour, timeinfo->tm_min,
		charInfo->position.x, charInfo->position.y, charInfo->position.z);

	return (mysql_query(&mysql, query) == 0);
}

bool MySQLDatabase::isValidCharName(string charname)
{
	if(charname == "")
		return false;

	MYSQL_RES* res;

	int8 query[1000];
	sprintf(query, "SELECT * FROM characters WHERE name = '%s'", charname.c_str());
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)))
	{
		uint64 result = mysql_num_rows(res);
		mysql_free_result(res);
		return (result == 0);
	}
	else return false;
}

bool MySQLDatabase::insertEmptyChar(GameClient* client)
{
	client->charInfo.characterID = generateCharId();
	int8 query[1000];
	sprintf(query, "INSERT INTO characters VALUES ('%u', '%u', '', '0', '0', '0', '0', '0', '4000', '', '0', '0', '0', '00-00-0000 00:00', '0', '0', '0', '0')",
		client->charInfo.characterID, client->charInfo.accountID);
	return (mysql_query(&mysql, query) == 0);
}

bool MySQLDatabase::setAccountCookie(string username, uint32 cookie)
{
	int8 query[1000];
	sprintf(query, "UPDATE accounts SET cookie = %u WHERE username = '%s'", cookie, username.c_str());
	return (mysql_query(&mysql, query) == 0);
}

bool MySQLDatabase::setAccountCookie(uint32 accountID, uint32 cookie)
{
	int8 query[1000];
	sprintf(query, "UPDATE accounts SET cookie = %u WHERE account_id = %u", cookie, accountID);
	return (mysql_query(&mysql, query) == 0);
}

int32 MySQLDatabase::getAccountByCharacter(CharacterInfo character)
{
	MYSQL_RES* res;

	int8 query[1000];
	sprintf(query, "SELECT account_id FROM characters WHERE character_id = %u", character.characterID);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)))
	{
		uint64 result = mysql_num_rows(res);
		mysql_free_result(res);
		return result;
	}
	else return -1;
}

int32 MySQLDatabase::getCharacterCount(uint32 accountID)
{
	MYSQL_RES* res;

	int8 query[1000];
	sprintf(query, "SELECT character_id FROM characters WHERE account_id = %u", accountID);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)))
	{
		uint64 result = mysql_num_rows(res);
		mysql_free_result(res);
		return result;
	}
	else return -1;
}

bool MySQLDatabase::getCharacters(uint32 accountID, vector<CharacterInfo>* characterInfos)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT character_id FROM characters WHERE account_id = %u", accountID);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			CharacterInfo charInfo;
			getCharacterInfo(atoi(row[0]), &charInfo);
			characterInfos->push_back(charInfo);
		}

		mysql_free_result(res);
		return true;
	}
	else return false;
}

bool MySQLDatabase::getCharacterInfo(uint32 characterID, CharacterInfo* info)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT character_id, account_id, name, race, class, level, sex, realm_id, last_connection, map_id, language, headmesh, size, voice, pos_x, pos_y, pos_z, lbinprv FROM characters WHERE character_id = %u", characterID);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		info->characterID = atoi(row[0]);
		info->accountID = atoi(row[1]);
		info->name = row[2];
		info->race = atoi(row[3]);
		info->Class = atoi(row[4]);
		info->level = atoi(row[5]);
		info->sex = atoi(row[6]);
		info->realmID = atoi(row[7]);
		info->lastConnection = row[8];
		info->map = atoi(row[9]);
		info->language = row[10];
		info->headmesh = atoi(row[11]);
		info->size = atoi(row[12]);
		info->voice = atoi(row[13]);
		info->position = Vector3D(atoi64(row[14]), atoi64(row[15]), atoi64(row[16]));
		info->lbinprv = atoi(row[17]);

		mysql_free_result(res);
		return true;
	}
	else return false;
}

string MySQLDatabase::getLevelAbilitys(uint32 classId, uint32 level)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT abilitys FROM const_abilitys WHERE class = '%u' AND level = '%u'", classId, level);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		string retValue = "";
		if(row[0])
			retValue = row[0];

		mysql_free_result(res);
		return retValue;
	}
	else return "";
}

string MySQLDatabase::getNewLevelAbilitys(uint32 classId, uint32 level)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT abilitysNew FROM const_abilitys WHERE class = '%u' AND level = '%u'", classId, level);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		string retValue = "";
		if(row[0])
			retValue = row[0];

		mysql_free_result(res);
		return retValue;
	}
	else return "";
}

string MySQLDatabase::getLevelSpells(uint32 classId, uint32 level)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT spells FROM const_abilitys WHERE class = '%u' AND level = '%u'", classId, level);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		string retValue = "";
		if(row[0])
			retValue = row[0];

		mysql_free_result(res);
		return retValue;
	}
	else return "";
}

string MySQLDatabase::getNewLevelSpells(uint32 classId, uint32 level)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT spellsNew FROM const_abilitys WHERE class = '%u' AND level = '%u'", classId, level);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		string retValue = "";
		if(row[0])
			retValue = row[0];

		mysql_free_result(res);
		return retValue;
	}
	else return "";
}

bool MySQLDatabase::getAbilityData(uint32 abilityId, Item* ability)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT id, slot, data0, data1, data2, data3, data4 FROM p_abilities WHERE id = '%u'", abilityId);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		ability->id = atoi64(row[0]);
		ability->iLevel = atoi64(row[1]);
		ability->data0 = atoi64(row[2]);
		ability->data1 = atoi64(row[3]);
		ability->data2 = atoi64(row[4]);
		ability->data3 = atoi64(row[5]);
		ability->data4 = atoi64(row[6]);

		mysql_free_result(res);
		return true;
	}
	else return false;
}

//Items
bool MySQLDatabase::insertItemEntry(uint32 characterID)
{
	if(!checkItemTable(characterID))
	{
		int8 query[1000];
		sprintf(query, "INSERT INTO player_items VALUES ('%u', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '0', '', '', '')",
			characterID);
		return (mysql_query(&mysql, query) == 0);
	}
	else
	{
		return false;
	}
}

bool MySQLDatabase::checkItemTable(uint32 characterID)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM player_items WHERE id = '%u'", characterID);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		mysql_free_result(res);
		return true;
	}
	else return false;
}

bool MySQLDatabase::getItemData(string item_name, Item* item)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT id, level, data0, data1, data2, data3, data4 FROM p_items WHERE name = '%s'", item_name.c_str());
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		item->id = atoi(row[0]);
		item->iLevel = atoi(row[1]);
		item->data0 = atoi64(row[2]);
		item->data1 = atoi64(row[3]);
		item->data2 = atoi64(row[4]);
		item->data3 = atoi64(row[5]);
		item->data4 = atoi64(row[6]);

		mysql_free_result(res);
		return true;
	}
	else return false;
}

bool MySQLDatabase::getItemData(uint32 itemId, Item* item)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT id, level, data0, data1, data2, data3, data4 FROM p_items WHERE id = '%u'", itemId);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		item->id = atoi64(row[0]);
		item->iLevel = atoi(row[1]);
		item->data0 = atoi64(row[2]);
		item->data1 = atoi64(row[3]);
		item->data2 = atoi64(row[4]);
		item->data3 = atoi64(row[5]);
		item->data4 = atoi64(row[6]);

		mysql_free_result(res);
		return true;
	}
	else
		return false;
}

bool MySQLDatabase::getNpcItems(uint32 npcId, vector<Item>* item)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM npc_items WHERE id = '%u'", npcId);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		Item itemInfo;
		for(uint32 i = 0; i < 18; i++)
		{
			if(atoi64(row[i + 1]) > 0)
			{
				uint32 item_id = atoi64(row[i + 1]);
				if(getItemData(item_id, &itemInfo))
				{
					switch((i+1))
					{
					case 0x01:
					case 0x02:
					case 0x03:
						{
							itemInfo.pos = (i + 1);
							break;
						}
					case 0x10:
					case 0x11:
						{
							itemInfo.pos = (i + 3);
							break;
						}
					default:
						{
							itemInfo.pos = (i + 2);
							break;
						}
					}
					item->push_back(itemInfo);
				}
				else
				{
					Log.Error("MySQL Query-Error at %i\nMaybe Item not in DB\n\n", (i + 1));
				}
			}
		}

		mysql_free_result(res);
		return true;
	}
	else
		return false;
}

bool MySQLDatabase::deleteCharacter(uint32 characterID)
{
	int8 query[1000];
	sprintf(query, "DELETE FROM characters WHERE character_id = %u", characterID);
	return (mysql_query(&mysql, query) == 0);
}

bool MySQLDatabase::deleteEmptyChar(uint32 accountID)
{
	int8 query[1000];
	sprintf(query, "DELETE FROM characters WHERE account_id = %u AND level = %i", accountID, 0);
	return (mysql_query(&mysql, query) == 0);
}

string MySQLDatabase::getCharacterName(uint32 characterID)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT name FROM characters WHERE character_id = %u", characterID);
	if((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (res->row_count > 0)) 
	{
		if((row = mysql_fetch_row(res)))
		{
			string result(row[0]);
			mysql_free_result(res);
			return result;
		}
		else return "";
	}
	else return "";
}

int32 MySQLDatabase::getNewCharacterID()
{
	MYSQL_RES* res;
	uint64 rows;

	if (!mysql_query(&mysql, "SELECT * FROM characters") && (res = mysql_store_result(&mysql))) 
	{
		if(!(rows = mysql_num_rows(res))) 
			return 1;
		else return rows + 1;
	}
	else return -1;
}

bool MySQLDatabase::getNpcs(uint32 mapId, vector<NPC>* npcs)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM npcs WHERE map = %u", mapId);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)))
	{
		while ((row = mysql_fetch_row(res)))
		{
			NPC npcInfo;
			npcInfo.npcId = atoi(row[0]);
			npcInfo.name = row[1];
			npcInfo.nameId = atoi64(row[2]);
			npcInfo.mapId = atoi(row[3]);
			npcInfo.action = atoi(row[4]);
			npcInfo.level = atoi(row[5]);
			npcInfo.fraction = atoi(row[6]);
			npcInfo.health = atoi64(row[7]);
			npcInfo.stamina = atoi64(row[8]);
			npcInfo.mana = atoi64(row[9]);
			npcInfo.getExp = atoi64(row[10]);
			npcInfo.extras = row[11];

			npcInfo.curHealth = npcInfo.health;
			npcInfo.curStamina = npcInfo.stamina;
			npcInfo.curMana = npcInfo.mana;

			getNpcMesh(npcInfo.npcId, &npcInfo);

			npcs->push_back(npcInfo);
		}
		mysql_free_result(res);
		return true;
	}
	else
		return false;
}

bool MySQLDatabase::getNpcDetail(uint32 npcID, NPC* spawn)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM npcs WHERE id = %u", npcID);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		spawn->name = row[1];
		spawn->nameId = atoi64(row[2]);
		spawn->mapId = atoi(row[3]);
		spawn->action = atoi(row[4]);
		spawn->level = atoi(row[5]);
		spawn->fraction = atoi(row[6]);
		spawn->health = atoi64(row[7]);
		spawn->stamina = atoi64(row[8]);
		spawn->mana = atoi64(row[9]);
		spawn->getExp = atoi64(row[10]);

			spawn->curHealth = spawn->health;
			spawn->curStamina = spawn->stamina;
			spawn->curMana = spawn->mana;
		mysql_free_result(res);
		if(getNpcMesh(npcID, spawn))
		{
			return true;
		}
		else
		{
			return false;
		}
	}
	else
	{
		return false;
	}
}

bool MySQLDatabase::getNpcMesh(uint32 npcID, NPC* spawn)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM npc_details WHERE id = %u", npcID);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		spawn->position = Vector3D(atoi64(row[1]), atoi64(row[2]), atoi64(row[3]));
		spawn->rotation = Vector3D(atoi64(row[4]), atoi64(row[5]), atoi64(row[6]));
		spawn->bodyMesh = atoi64(row[7]);
		spawn->headMesh = atoi64(row[8]);
		spawn->hairMesh = atoi64(row[9]);
		spawn->beardMesh = atoi64(row[10]);
		spawn->size = atoi64(row[11]);

		mysql_free_result(res);
		return true;
	}
	else
	{
		Log.Error("ERROR\n%s\n\n", query);
		return false;
	}
}

uint32 MySQLDatabase::getNpcFraction(uint32 npcId)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT fraction FROM npcs WHERE id = %u", npcId);
	if (mysql_query(&mysql, query) == 0 && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		uint64 result = atoi(row[0]);
		mysql_free_result(res);
		return result;
	}
	else return 0;
}

bool MySQLDatabase::getMaxStats(GameClient* clientInfo)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM const_stats WHERE level = %u AND race = %u AND class = %u", clientInfo->charInfo.level, clientInfo->charInfo.race, clientInfo->charInfo.Class);
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		clientInfo->charInfo.maxStats.health = atoi64(row[3]);
		clientInfo->charInfo.maxStats.mana = atoi64(row[4]);
		clientInfo->charInfo.maxStats.stamina = atoi64(row[5]);
		clientInfo->charInfo.maxStats.exp = atoi64(row[6]);
		clientInfo->charInfo.PlayerAttributes.Constitution = atoi64(row[9]);
		clientInfo->charInfo.PlayerAttributes.Dexterity = atoi64(row[10]);
		clientInfo->charInfo.PlayerAttributes.Intelligence = atoi64(row[8]);
		clientInfo->charInfo.PlayerAttributes.Strength = atoi64(row[7]);
		clientInfo->charInfo.PlayerAttributes.Wisdom = atoi64(row[11]);
		clientInfo->charInfo.maxStats.expOffset = atoi64(row[12]);
		mysql_free_result(res);
		return true;
	}
	else return false;
}

bool MySQLDatabase::getMapDetails(string map_name, teleport* info)
{
	MYSQL_RES* res;
	MYSQL_ROW row;

	int8 query[1000];
	sprintf(query, "SELECT * FROM maps WHERE map_name = '%s'", map_name.c_str());
	if ((mysql_query(&mysql, query) == 0) && (res = mysql_store_result(&mysql)) && (row = mysql_fetch_row(res)))
	{
		info->mapID = atoi64(row[0]);
		info->position = Vector3D(atoi64(row[2]), atoi64(row[3]), atoi64(row[4]));
		info->rotation = Vector3D(atoi64(row[5]), atoi64(row[6]), atoi64(row[7]));

		mysql_free_result(res);
		return true;
	}
	else return false;
}