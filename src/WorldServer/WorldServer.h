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

#ifndef WORLDSERVER_H
#define WORLDSERVER_H

#include "../Common/Common.h"
#include "../Common/Settings.h"
#include "../Common/MysqlDatabase.h"
#include "../Common/Functions.h"
#include "../Common/PacketBuffer.h"
#include "../Common/Packet.h"
#include "../Common/Networking.h"
#include "../Common/Structs.h"
#include "../Common/Logger.h"
#include "../Common/Timer.h"
#include "WorldServerOpcodes.h"
#include "PassBlobs/PassblobHandler.h"
#include "SendPackets/SendPackets.h"

#ifdef WINDOWS
#include <Windows.h>
#else
#include <sys/time.h>
#endif

using namespace std;

#define SPRINT_DECREASE 55

namespace WorldServer
{
	//NPC Combat System
	void HandleNpcCombat(GlobalTable * GTable);
	//--------------
	void LoadNearChars(GameClient* client, vector<GameClient*>* clientList);
	bool checkAlreadySpawned(GameClient* client, uint32 characterId);
	//CombatHandler
	void HitWithSpell(GlobalTable* GTable, Packet* packet);
	void sendSpellData(GlobalTable* GTable);
	uint32 checkEffectSpell(GlobalTable* GTable, uint32 effectId);
	bool checkHasEffect(GlobalTable* GTable, uint32 effectId);
	void HitEnemy(GlobalTable* GTable, Packet* packet);
	void increaseExp(GameClient* client);
	void increaseLevel(GameClient* client, int lvl = 0);
	bool checkEnemyInRange(GlobalTable* GTable);
	void enemyRunToPlayer(GlobalTable* GTable);
	uint32 getCoordToPlayer(uint32 player, uint32 npc);
	void moveEnemyToPlayer(GlobalTable * GTable, uint32 target);
	time_t getCurrentTime();

	NPC getNpcData(GlobalTable* GTable, uint32 npcId);
	Spells getSpellData(GlobalTable* GTable, uint32 spellId);
	void updateNpcData(GlobalTable* GTable, NPC curNpc);
	Vector3D setRange(GameClient* client, NPC curNpc);
	Vector3D calculateWay(GameClient* client, NPC curNpc);
	Vector3D getCurrentPosition(Vector3D range, uint32 diff, uint32 speed);

	bool checkInRange(Vector3D target, Vector3D npc);
	void npcHitPlayer(GlobalTable* GTable, uint32 npcId);

	void LoadNearNPCs(GlobalTable* GTable);
	bool spawnNPC(NPC tmpNPC, GlobalTable* GTable);
	bool removeNPC(GameClient* client, uint32 npcId, uint32 instance);
	bool checkInRange(Vector3D charPos, Vector3D npcPos);
	bool checkAlreadySpawned(vector<uint32>* spawned, uint32 npcId);
	uint8 GetDigitValue(int8 digit);
	void eraseAllNpcs(GameClient* client);

	void GameAgentHandler(Packet* packet, GlobalTable* GTable, vector<GameClient*>* clientList);
	void GameCharAgentHandler(Packet* packet, GlobalTable* GTable);
	void GameCharAgentProjectHandler(Packet* packet, GameClient* client);

	void HandleCommand(GlobalTable* GTable, string command, vector<GameClient*>* clientList);
	void addCharLevel(string level,GameClient *Client);
	void addItem(string itemname, GameClient* client);
	void addMoney(uint32 cash, GameClient* client);
	void teleportChar(string zone, GameClient* client, vector<GameClient*>* clientList);

	//Start
	void HandleClient(GlobalTable* GTable);

	//AbilityHandler
	void checkNewAbilities(GameClient* client);
	void checkAbility(GameClient* client);
	void addAbility(GameClient* client, uint32 abilityId, uint32 level, uint32 pos = 0);
	void addSpell(GameClient* client, uint32 SpellId);

	//CharStatsHandler
	struct CharStat {
		void sendCharSpawn(GameClient* client);
		void sendCharacterStats(GameClient* client);
		uint64 addMaxHealth(uint32 value);
		uint64 addHealth(uint32 value);
		uint64 addMaxMana(uint32 value);
		uint64 addMana(uint32 value);
		uint64 addMaxStamina(uint32 value);
		uint64 addStamina(uint32 value);
		//Attributes
		uint64 addInteligence(uint32 value);
		uint64 addStrength(uint32 value);
		uint64 addConstitution(uint32 value);
		uint64 addDexterity(uint32 value);
		uint64 addWisdom(uint32 value);
		//---------
		uint64 addLevel(uint32 value);
		uint64 addExp(uint32 value);
		uint64 addMaxExp(uint32 value);
		uint64 addExpOffset(uint32 value);
		uint64 addSkillpoints(uint32 value);
		uint64 addPvpLevel(uint32 value);
		uint64 addPvpExp(uint32 value);
		uint64 addPvpExpOffset(uint32 value);
		uint64 addPvpMaxExp(uint32 value);
		uint64 addMurderPoints(uint32 value);
		uint64 addCoinsHigherPart(uint64 value);
		uint64 addCoinsLowerPart(uint64 value);
		uint64 addSiegeLose(uint32 value);
		uint64 addSiegeWin(uint32 value);
		uint64 addCaptureTheSkullLose(uint32 value);
		uint64 addCaptureTheSkullWin(uint32 value);
		uint64 addAnnihilationLose(uint32 value);
		uint64 addAnnihilationWin(uint32 value);
		uint64 addDeath(uint32 value);
		uint64 addKillingBlows(uint32 value);
		uint8 getSexRace(uint32 sex, uint32 race);

		void setStartStats(GameClient* client);

		void setItemStats(GameClient* client, uint32 placeId, uint32 itemId);
	};
}
#endif
