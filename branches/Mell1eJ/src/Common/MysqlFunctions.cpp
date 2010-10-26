/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

#include "MysqlFunctions.h"

MySQLFunctions::MySQLFunctions() : Singleton<MySQLFunctions>()
{
	//
}

bool MySQLFunctions::CheckLogin(string username, string password)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT * FROM accounts WHERE username = '%s' AND password = '%s' LIMIT 1", username.c_str(), password.c_str());

	MySQLDB->executeSynchronousQuery(&q); 
	if(q.succes())
		return (q.numRows() != 0);			
	else
		return false;
}

uint32 MySQLFunctions::GetAccountID(string username)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT account_id FROM accounts WHERE username = '%s' LIMIT 1", username.c_str());

	MySQLDB->executeSynchronousQuery(&q); 
	if(q.succes())
		return q.getUint32(0);				
	else
		return 0;
}

bool MySQLFunctions::IsAccountBanned(uint32 nClientInst)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT state FROM accounts WHERE account_id = %u LIMIT 1", nClientInst);

	MySQLDB->executeSynchronousQuery(&q); 
	if(q.succes())
		return (q.getUint32(0) != 0);
	else 
		return 0;
}

uint32 MySQLFunctions::GetNewCharacterId()
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT MAX(character_id) FROM characters");

	MySQLDB->executeSynchronousQuery(&q); 
	if(q.succes())
		return q.getUint32(0) + 1;				
	else
		return 0;
}

bool MySQLFunctions::SetAccountCookie(uint32 nClientInst, uint32 cookie)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("UPDATE accounts SET cookie = %u WHERE account_id = %u", cookie, nClientInst);

	MySQLDB->executeSynchronousQuery(&q);
	return q.succes();
}

bool MySQLFunctions::UpdateLastInfo(uint32 nClientInst, string ipAddress)
{
	time_t rawtime;
	time(&rawtime);
	tm* timeinfo = localtime(&rawtime);

	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("UPDATE accounts SET last_ip = '%s', last_connection = '%02i/%02i/%04i %02i:%02i' WHERE account_id = %u",
		ipAddress.c_str(), timeinfo->tm_mday, timeinfo->tm_mon + 1, timeinfo->tm_year + 1900, timeinfo->tm_hour, timeinfo->tm_min, nClientInst);

	MySQLDB->executeSynchronousQuery(&q);
	return q.succes();
}

bool MySQLFunctions::GetRealm(uint32 realmID, RealmInfo *realm)
{
	//

	return false;
}

bool MySQLFunctions::GetRealms(vector<RealmInfo> *realms)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT realm_id FROM realms");

	MySQLDB->executeSynchronousQuery(&q);
	if(q.succes())
	{
		do
		{
			RealmInfo realmInfo;
			if(GetRealm(q.getUint32(0), &realmInfo))
				realms->push_back(realmInfo);
		}
		while(q.nextRow());

		return true;
	}
	else return false;
}

bool MySQLFunctions::GetCharacter(uint32 characterID, CharacterInfo *character)
{
	//

	return false;
}

bool MySQLFunctions::GetCharacters(uint32 accountID, vector<CharacterInfo> *characters)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT character_id FROM characters WHERE account_id = %u", accountID);

	MySQLDB->executeSynchronousQuery(&q);
	if(q.succes())
	{
		do
		{
			CharacterInfo charInfo;
			if(GetCharacter(q.getUint32(0), &charInfo))
				characters->push_back(charInfo);
		}
		while(q.nextRow());

		return true;
	}
	else return false;
}