/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

#include "WorldServer.h"

void WorldServer::checkNewAbilities(GameClient* client)
{
	//Loads the new Abilities and Spells for the Class and Level
	string levelAbilitys = Database.getNewLevelAbilitys(client->charInfo.Class, client->charInfo.level);
	string levelSpells = Database.getNewLevelSpells(client->charInfo.Class, client->charInfo.level);


	//Abilitys
	if(levelAbilitys.size() > 0)
	{
		vector<string> abilitys = String::splitString(levelAbilitys, "-");
		for(uint32 i = 0; i < abilitys.size(); i++)
		{
			vector<string> temp = String::splitString(abilitys.at(i), "_");
			addAbility(client, atoi64(temp.at(0).c_str()), atoi64(temp.at(1).c_str()));
		}
	}
	
	if(levelSpells.size() > 0)
	{
		//Spells
		vector<string> spellsx = String::splitString(levelSpells, "-");
		for(uint32 i = 0; i < spellsx.size(); i++)
		{
			addSpell(client, atoi64(spellsx.at(i).c_str()));
		}
	}
}

//This function is only for login user (or teleporting)
void WorldServer::checkAbility(GameClient* client)
{
	//Loads the current Abilities and Spells for the Class and Level
	string levelAbilitys = Database.getLevelAbilitys(client->charInfo.Class, client->charInfo.level);
	string levelSpells = Database.getLevelSpells(client->charInfo.Class, client->charInfo.level);
	
	//Abilitys
	if(levelAbilitys.size() > 0)
	{
		vector<string> abilitys = String::splitString(levelAbilitys, "-");
		for(uint32 i = 0; i < abilitys.size(); i++)
		{
			vector<string> temp = String::splitString(abilitys.at(i), "_");
			addAbility(client, atoi64(temp.at(0).c_str()), atoi64(temp.at(1).c_str()));
		}
	}
	/*
	if(levelSpells.size() > 0)
	{
		//Spells
		vector<string> spellsx = String::splitString(levelSpells, "-");
		for(uint32 i = 0; i < spellsx.size(); i++)
		{
			addSpell(client, atoi64(spellsx.at(i).c_str()));
		}
	}
	//*/
}

//Adds a Ability to the player
void WorldServer::addAbility(GameClient* client, uint32 abilityId, uint32 level, uint32 iLevel)
{
	//Abilitys work at opcode 0x4d (GameCharAgent)
	Item abilityData; //Use the Struct of Items
	if(Database.getAbilityData(abilityId, &abilityData)) //Check if Ability is in the Database
	{
		PacketBuffer aBuffer(500);
		aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x4d);
		aBuffer.write<uint32>(iLevel ? iLevel : abilityData.iLevel); //Level is used for slot iLevel if null load position from the db if > 0 load debug pos
		aBuffer.write<uint32>(abilityData.id);
		aBuffer.write<uint32>(abilityData.id);
		aBuffer.write<uint32>(level);
		aBuffer.write<uint32>(abilityData.data0);

		aBuffer.write<uint32>(abilityData.data1);
		aBuffer.write<uint32>(abilityData.data2);
		aBuffer.write<uint32>(abilityData.data3);
		aBuffer.write<uint32>(abilityData.data4);

		aBuffer.write<uint32>(abilityData.data1);
		aBuffer.write<uint32>(abilityData.data2);
		aBuffer.write<uint32>(abilityData.data3);
		aBuffer.write<uint32>(abilityData.data4);

		aBuffer.write<uint32>(level);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint8>(0);
		
		aBuffer.doItAll(client->clientSocket);
	}
	else
	{
		Log.Error("Ability %u not found in database!\n", abilityId);
	}
}

//Adds a Spell to the player
void WorldServer::addSpell(GameClient* client, uint32 SpellId)
{
	//Abilitys work at opcode 0x09 (GameCharAgent)
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x09);
	aBuffer.write<uint32>(SpellId);
	aBuffer.doItAll(client->clientSocket);
}

