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

#ifndef CLIENT_H
#define CLIENT_H

#include <string>

#include "Common.h"
#include "Vector3D.h"
#include "Functions.h"
#include "Structs.h"

struct GameClient;
struct CharacterInfo
{
	uint32 characterID;
	uint32 accountID;
	string name;
	uint32 race;
	uint32 Class; 
	uint32 level;
	uint32 sex;
	uint32 realmID;
	string lastConnection;
	uint32 map;
	uint32 headmesh;
	uint32 size;
	uint32 voice;
	string language;
	uint32 lbinprv;

	Vector3D position;
	Vector3D rotation;

	int32 curExp;
	CharacterState state;
	Combat combat;

	bool isExhausted;

	lvlStats maxStats;
	lvlStats curMaxStats;
	int32 hp, mana, stamina;

	struct Attributes
	{
		uint16 Strength;
		uint16 Intelligence;
		uint16 Constitution; //+8health
		uint16 Dexterity;
		uint16 Wisdom;

		inline Attributes()
		{
			Strength = 1;
			Intelligence = 1;
			Constitution = 1;
			Dexterity = 1;
			Wisdom = 1;

		}

		void CalcAttrb(GameClient* client);
	}PlayerAttributes;

	//Add this later into ItemHandler.cpp Item stuff here generaly
	struct POutfit
	{
		uint32 ID_NECK;
		uint32 ID_HEAD;
		uint32 ID_TOKKENBUTTON;
		uint32 ID_CHEST;
		uint32 ID_MAINWEAPON_LEFT;
		uint32 ID_BELT;
		uint32 ID_OFFHAND_LEFT;
		uint32 ID_RING_RIGHT;
		uint32 ID_RING_LEFT;
		uint32 ID_CLOAK;
		uint32 ID_SHOULDER;
		uint32 ID_BOOTS;
		uint32 ID_HANDS;
		uint32 ID_LEGS;
		uint32 ID_WRIST;
		uint32 ID_MAINWEAPON_RIGHT;
		uint32 ID_OFFHAND_RIGHT;

	}Outfit;

	enum OutfitIDS
	{
		ID_NECK = 0x01,
		ID_HEAD = 0x02,
		ID_TOKKENBUTTON = 0x03,
		ID_CHEST = 0x05,
		ID_MAINWEAPON_LEFT = 0x06,
		ID_BELT = 0x07,
		ID_OFFHAND_LEFT = 0x08,
		ID_RING_RIGHT = 0x0a,
		ID_RING_LEFT = 0x09,
		ID_CLOAK = 0x0b,
		ID_SHOULDER = 0x0c,
		ID_BOOTS = 0x0e,
		ID_HANDS = 0x0f,
		ID_LEGS = 0x10,
		ID_WRIST = 0x12,
		ID_MAINWEAPON_RIGHT = 0x14,
		ID_OFFHAND_RIGHT = 0x15
	};

	void WearItem(GameClient* client, uint32 id, uint32 pos);
	void UnWearItem(GameClient* client, uint32 pos);

	void Update(GameClient* client);
	void RegenerateStamina(GameClient* client);
	void RegenerateMana(GameClient* client);
	void RegenerateHealth(GameClient* client);
	void ApplyAura(GameClient* client, uint32 id);
	void RemoveAura(GameClient* client, uint32 id);
	void InitalizeStatupStats(GameClient* client);
};

struct GameClient
{
	PacketBuffer receiveBuffer;
	bool initializedBuffer;

	z_stream* receiveStream;
	z_stream* sendStream;

	SOCKET clientSocket;
	string ipAddress;
	bool isConnected;

	string authChallenge;
	string username;

	uint32 nClientInst;
	uint32 nCookie;

	uint32 vc;

	uint32* m_o1c;
	uint32* m_data1c;

	uint32 state;
	uint32 counter;

	vector<uint32>* loadedChar;
	vector<uint32>* spawnedMobs;

	CharacterInfo charInfo;

	vector<NPC> datastoreNpcs;

	ulong threadID;

	GameClient(SOCKET socket);
	~GameClient();

	bool Send(PacketBuffer* buffer, bool compressed);
	bool Receive(bool compressed);
	Packet* getNextPacket(bool compressed);
};

struct GlobalTable
{
	vector<Spells> SPELLS;
	GameClient* client;
	vector<NPC> NPCS;
	void* socket;
};
#endif