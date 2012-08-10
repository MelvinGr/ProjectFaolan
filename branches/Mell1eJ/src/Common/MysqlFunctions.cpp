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

#include "MysqlFunctions.h"

using namespace std;

bool MySQLFunctions::CheckLogin(const std::string& username, const std::string& password)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT * FROM accounts WHERE username = '%s' AND password = '%s' LIMIT 1", username.c_str(), password.c_str());

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	bool value = false;
	if(q.succes())
		value = q.fetchRow();

	return value;
}

int32 MySQLFunctions::GetAccountID(const std::string& username)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT account_id FROM accounts WHERE username = '%s' LIMIT 1", username.c_str());

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	int32 value = -1;
	if(q.succes())
	{
		if(q.fetchRow())
			value = q.getUint32(0);		
	}

	return value;
}

bool MySQLFunctions::IsAccountBanned(uint32 nClientInst)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT state FROM accounts WHERE account_id = %u LIMIT 1", nClientInst);

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	bool value = true;
	if(q.succes())
	{
		if(q.fetchRow())
			value = (q.getUint32(0) != 0);		
	}

	return value;
}

int32 MySQLFunctions::GetNewCharacterId()
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT MAX(character_id) FROM characters");

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	int32 value = -1;
	if(q.succes())
	{
		if(q.fetchRow())
			value = (q.getUint32(0) + 1);		
	}

	return value;
}

int32 MySQLFunctions::GetAccountCookie(uint32 nClientInst)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT cookie FROM accounts WHERE account_id = '%u' LIMIT 1", nClientInst);

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	int32 value = -1;
	if(q.succes())
	{
		if(q.fetchRow())
			value = q.getUint32(0);		
	}

	return value;
}

bool MySQLFunctions::SetAccountCookie(uint32 nClientInst, uint32 cookie)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("UPDATE accounts SET cookie = %u WHERE account_id = %u", cookie, nClientInst);

	MysqlDB->executeSynchronousQuery(&q);
	Sleep(50);

	return q.succes();
}

bool MySQLFunctions::UpdateLastInfo(uint32 nClientInst, const std::string& ipAddress)
{
	time_t rawtime;
	time(&rawtime);
	tm* timeinfo = localtime(&rawtime);

	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("UPDATE accounts SET last_ip = '%s', last_connection = '%02i/%02i/%04i %02i:%02i' WHERE account_id = %u",
		ipAddress.c_str(), timeinfo->tm_mday, timeinfo->tm_mon + 1, timeinfo->tm_year + 1900, 
		timeinfo->tm_hour, timeinfo->tm_min, nClientInst);

	MysqlDB->executeSynchronousQuery(&q);
	Sleep(50);

	return q.succes();
}

bool MySQLFunctions::GetRealm(uint32 realmID, RealmInfo &realm)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT * FROM realms WHERE realm_id=%u", realmID);

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	if(!q.succes())
		return false;

	if(!q.fetchRow())
		return false;

	realm.realmID = q.getUint32();
	realm.realmName = q.getString();
	realm.realmType = q.getUint32();

	realm.onlineStatus = q.getUint32();
	realm.loadStatus = q.getUint32();

	realm.csPlayerAgentIPAddress = q.getString();
	realm.csPlayerAgentPort = q.getUint32();

	realm.agentServerIPAddress = q.getString();
	realm.agentServerPort = q.getUint32();

	realm.worldServerIPAddress = q.getString();
	realm.worldServerPort = q.getUint32();

	return true;
}

bool MySQLFunctions::GetRealms(vector<RealmInfo> &realms)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT realm_id FROM realms");

	MysqlDB->executeSynchronousQuery(&q);
	Sleep(50);

	if(!q.succes())
		return false;

	if(!q.fetchRow())
		return false;

	do
	{
		RealmInfo realmInfo;
		if(GetRealm(q.getUint32(0), realmInfo))
			realms.push_back(realmInfo);
	}
	while(q.nextRow());

	return true;
}

bool MySQLFunctions::GetCharacter(uint32 characterID, CharacterInfo &character)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT * FROM characters WHERE character_id=%u", characterID);

	MysqlDB->executeSynchronousQuery(&q); 
	Sleep(50);

	if(!q.succes())
		return false;

	if(!q.fetchRow())
		return false;

	character.characterID = q.getUint32();
	character.accountID = q.getUint32();
	character.name = q.getString();
	character.race = q.getUint32();
	character.Class = q.getUint32();
	character.level = q.getUint32();
	character.sex = q.getUint32();
	character.realmID = q.getUint32();
	character.map = q.getUint32();
	character.language = q.getUint32();
	character.headmesh = q.getUint32();
	character.size = q.getUint32();
	character.voice = q.getUint32();
	character.lastConnection = q.getString();
	character.position = Vector3D(q.getUint64(), q.getUint64(), q.getUint64());		
	character.lbinprv = q.getUint32();

	return true;
}

bool MySQLFunctions::GetCharacters(uint32 accountID, vector<CharacterInfo> &characters)
{
	MysqlQuery q(MysqlQuery::NO_CALLBACK, MysqlQuery::HAS_RESULT);
	q.setQueryText("SELECT character_id FROM characters WHERE account_id = %u", accountID);

	MysqlDB->executeSynchronousQuery(&q);
	Sleep(50);

	if(!q.succes())
		return false;

	if(!q.fetchRow())
		return false;

	do
	{
		CharacterInfo charInfo;
		if(GetCharacter(q.getUint32(0), charInfo))
			characters.push_back(charInfo);
	}
	while(q.nextRow());

	return true;
}

bool MySQLFunctions::DeleteCharacter(uint32 characterID)
{
	return true;
}