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

#ifndef STRUCTS_H
#define STRUCTS_H

#include <string>

#include "Common.h"
#include "Vector3D.h"
#include "Functions.h"

using namespace std;

enum CharacterState
{
	RESTING = 0,
	WALKING,
	SWIMING,
	SPRINTING,
	SPRINTJMP,
	JUMPING,
	NONE
};

struct RealmInfo
{
	uint32 realmID;
	string realmName;
	uint16 realmType; // 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask

	uint32 onlineStatus; //2 = online, other num = offline
	uint32 loadStatus; // 0 = medium load 1 = medium load 2 = heavy load 3 = full
	uint32 serverActive;

	string agentServerIPAddress;
	uint32 agentServerPort;

	string csPlayerAgentIPAddress;
	uint32 csPlayerAgentPort;

	string worldServerIPAddress;
	uint32 worldServerPort;
};

struct AccountInfo
{
	uint32 accountID;
	string username;
	string mail;
	uint32 kind;
	uint32 characterCount;
	uint32 cookie;
};

struct lvlStats
{
	int32 health;
	int32 mana;
	int32 stamina;
	int32 exp;
	uint32 expOffset;
};

struct effectSpell
{
	uint32 effectId;
	time_t endEffectTime;
};

struct Combat
{
	uint32 target;
	bool inCombat;
	time_t lastcheck;
	double attackSpeed;
	time_t activateSpell;
	uint32 spellId;

	vector<effectSpell> effect;
};

struct teleport
{
	uint32 mapID;
	Vector3D position;
	Vector3D rotation;
};

struct Item
{
	uint32 id;
	uint32 iLevel; //item level
	uint32 data0;
	uint32 data1; 
	uint32 data2;
	uint32 data3;
	uint32 data4;
	uint32 pos;
};

struct NPCMove
{
	bool isMoving;
	tm* last_waypoint;
	int32 milliseconds;
	Vector3D range;
};

struct NPC
{
	uint32 npcId;
	string name;
	uint32 nameId;
	uint32 mapId;
	uint32 typ;
	Vector3D position;
	struct RotationStruct
	{
		uint32 a;
		uint32 b;
		uint32 c;
		uint32 d;
	}rotation;
	string extras;
	uint32 action;
	uint32 level;
	uint32 fraction;
	uint32 health;
	uint32 stamina;
	uint32 mana;
	uint32 bodyMesh;
	uint32 headMesh;
	uint32 hairMesh;
	uint32 beardMesh;
	uint32 size;
	uint32 getExp;
	uint32 curHealth;
	uint32 curStamina;
	uint32 curMana;
	uint8 gender;
	uint8 race;

	bool attackMode;
	NPCMove move;
	Combat combat;
};

struct Spells
{
	uint32 id;
	uint32 level;
	string name;
	uint32 castTime;
	uint32 minDmg;
	uint32 maxDmg;
	uint32 costMana;
	string effectSpell;
};

#endif