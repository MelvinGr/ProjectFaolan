/*
Project Faolan a Simple and Free Servoer Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

This program is free softvtare: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Softvtare Foundation, either voersion 3 of the License, or
(at your option) any later voersion.

This program is distributed in the hope that it vtill be useful,
but WITHOUT ANY WARRANTY; without evoen the implied vtarranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have receivoed a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#include "WorldServer.h"

void WorldServer::HitWithSpell(GlobalTable* GTable, Packet* packet)
{
	GameClient* client = GTable->client;
	NPC curCombat = getNpcData(GTable, client->charInfo.combat.target);

	//uint32 unk1 = packet->data->read<uint32>();
	//uint32 cins = packet->data->read<uint32>();
	//uint16 unk2 = packet->data->read<uint16>();
	uint32 unk3 = packet->data->read<uint32>(); //00000004
	uint32 unk4 = packet->data->read<uint32>(); 
	uint32 unk5 = packet->data->read<uint32>(); //00000001 - normaly it should 7 (maybe counter, because it increase)
	uint32 unk6 = packet->data->read<uint32>(); //0
	uint32 unk7 = packet->data->read<uint32>(); //0a
	uint32 unk8 = packet->data->read<uint32>(); //0
	uint32 unk9 = packet->data->read<uint32>(); //0
	uint32 unk10 = packet->data->read<uint32>(); //0
	uint32 unk11 = packet->data->read<uint32>(); //0
	uint32 unk12 = packet->data->read<uint32>(); //0
	uint32 unk13 = packet->data->read<uint32>(); //0000c350
	uint32 target = packet->data->read<uint32>(); 
	uint32 unk15 = packet->data->read<uint32>(); //0
	uint32 unk16 = packet->data->read<uint32>(); //69 74 61 00
	uint32 unk17 = packet->data->read<uint32>(); //0
	uint32 spellId = packet->data->read<uint32>(); //0
	uint8 unk19 = packet->data->read<uint8>(); //0
	uint32 unk20 = packet->data->read<uint32>(); //0
	uint32 unk21 = packet->data->read<uint32>(); //0
	uint32 unk22 = packet->data->read<uint32>(); //0
	uint8 unk23 = packet->data->read<uint8>(); //0
	
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000060);
	aBuffer.write<uint32>(0x642cd3d6);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0004);
	aBuffer.write<uint32>(unk3);
	aBuffer.write<uint32>(unk4);
	aBuffer.write<uint32>(unk5);
	aBuffer.write<uint32>(unk6);
	aBuffer.write<uint32>(unk7);
	aBuffer.write<uint32>(unk8);
	aBuffer.write<uint32>(unk9);
	aBuffer.write<uint32>(unk10);
	aBuffer.write<uint32>(unk11);
	aBuffer.write<uint32>(unk12);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(target);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(unk16);
	aBuffer.write<uint32>(unk17);
	aBuffer.write<uint32>(spellId);
	aBuffer.write<uint8>(1);
	aBuffer.write<uint32>(unk20);
	aBuffer.write<uint32>(unk21);
	aBuffer.write<uint32>(unk22);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000060);
	aBuffer.write<uint32>(0x642cd3d6);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x000a);
	aBuffer.write<uint32>(unk3);
	aBuffer.write<uint32>(unk4);
	aBuffer.write<uint32>(unk5);
	aBuffer.write<uint32>(unk6);
	aBuffer.write<uint32>(unk7);
	aBuffer.write<uint32>(unk8);
	aBuffer.write<uint32>(unk9);
	aBuffer.write<uint32>(unk10);
	aBuffer.write<uint32>(unk11);
	aBuffer.write<uint32>(unk12);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(target);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(unk16);
	aBuffer.write<uint32>(unk17);
	aBuffer.write<uint32>(spellId);
	aBuffer.write<uint8>(1);
	aBuffer.write<uint32>(unk20);
	aBuffer.write<uint32>(unk21);
	aBuffer.write<uint32>(unk22);
	aBuffer.write<uint8>(1);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	//*/
	/*
	Set spell activation time.
	When time is reached send next packets
	*/
	time_t curTime;
	time(&curTime);

	#ifdef WINDOWS
		SYSTEMTIME now;
		GetSystemTime(&now);
		curTime = (curTime * 1000) + now.wMilliseconds;
	#else
		timeval now;
		gettimeofday(&now, 0);
		curTime = (curTime * 1000) + now.tv_usec;
	#endif

	Spells tmpSpell = getSpellData(GTable, spellId);

	client->charInfo.combat.target = target;
	client->charInfo.combat.activateSpell = curTime+tmpSpell.castTime;
	client->charInfo.combat.spellId = spellId;
}

void WorldServer::sendSpellData(GlobalTable* GTable)
{
	GameClient* client = GTable->client;
	Spells tmpSpell = getSpellData(GTable, client->charInfo.combat.spellId);

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000021);
	aBuffer.write<uint32>(0x0d155695);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000016);
	aBuffer.write<uint32>(0x1745ef6c);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001e);
	aBuffer.write<uint32>(0x8a01e5a1);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0000);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000016);
	aBuffer.write<uint32>(0x1745ef6c);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000019);
	aBuffer.write<uint32>(0x6b0aec58);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	srand(time(NULL));
	uint32 dmgP = rand() % 100 + client->charInfo.level;
	double mulP = ((dmgP / 100) * (tmpSpell.maxDmg - tmpSpell.minDmg))+tmpSpell.minDmg;
	uint32 dmg = mulP;
	//Packet for Damage
	aBuffer = PacketBuffer(5000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000004d);
	aBuffer.write<uint32>(0xe9f24184);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x000012e5);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x0000001b);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0xffffffff - dmg);//0xFFFFFC17); //dmg value - 0xffffffff
	aBuffer.write<uint32>(0x00000004);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(client->charInfo.combat.spellId);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	NPC curNPC = getNpcData(GTable, GTable->client->charInfo.combat.target);
	
	curNPC.curHealth -= dmg;
	updateNpcData(GTable, curNPC);
	if(tmpSpell.effectSpell.size() > 0)
	{
		vector<string> spellsx = String::splitString(tmpSpell.effectSpell, "-");
		uint32 effectId = atoi64(spellsx[0].c_str());

		Spells effectS = getSpellData(GTable, effectId);

		effectSpell tmpEffect;
		tmpEffect.effectId = checkEffectSpell(GTable, effectId);
		time_t curTime = getCurrentTime();
		tmpEffect.endEffectTime = curTime + 9000;

		curNPC = getNpcData(GTable, GTable->client->charInfo.combat.target);
		
		if(!checkHasEffect(GTable, tmpEffect.effectId))
			curNPC.combat.effect.push_back(tmpEffect);

		updateNpcData(GTable, curNPC);

		aBuffer = PacketBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000004c);
		aBuffer.write<uint32>(0x6b87e316);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->charInfo.combat.target);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(tmpEffect.effectId);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->charInfo.combat.target);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(0x00038400);
		aBuffer.write<uint8>(0x01);
		aBuffer.write<uint32>(0x00000001);
		aBuffer.write<uint32>(0x00000000);
		aBuffer.write<uint32>(tmpEffect.effectId);
		aBuffer.write<uint32>(0x00000001);
		aBuffer.write<uint32>(0x00000000);
		aBuffer.write<uint32>(0x00000001);
		aBuffer.write<uint32>(0x00000000);
		aBuffer.write<uint32>(0x479d3d1f);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);

		if(effectS.minDmg > 0)
		{
			aBuffer = PacketBuffer(500);
			aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
			aBuffer.write<uint32>(0x0000004d);
			aBuffer.write<uint32>(0xe9f24184);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->charInfo.combat.target);
			aBuffer.write<uint8>(0x00);
			aBuffer.write<uint32>(0x0000124a);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0x0000001b);
			aBuffer.write<uint32>(0x00000002);
			aBuffer.write<uint32>(0xffffffec);
			aBuffer.write<uint32>(0x00000004);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<uint32>(0x00000001);
			aBuffer.write<uint32>(0x00000000);
			aBuffer.write<uint32>(tmpEffect.effectId);
			aBuffer.write<uint32>(0x00000001);
			aBuffer.write<uint32>(0x00000000);
			aBuffer.write<uint32>(0x3e4f4f3c);
			aBuffer.doItAll(client->clientSocket);

			curNPC.curHealth -= 100;
		}
	}
	
	GTable->client->charInfo.mana -= tmpSpell.costMana;
	//Set new Mana
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x000001fa);
	aBuffer.write<uint32>(GTable->client->charInfo.mana * 100);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	uint32 checkHealth = curNPC.curHealth & 0xf0000000;
	if(curNPC.curHealth <= 0 || checkHealth == 0xf0000000)
	{
		client->charInfo.curExp += curNPC.getExp;
		curNPC.combat.inCombat = false;
		curNPC.combat.target = 0;

		increaseExp(client);

		aBuffer = PacketBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000004a);
		aBuffer.write<uint32>(0x642cd3d6);	// to enemy down
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->charInfo.combat.target);
		aBuffer.write<uint16>(0x0004);
		aBuffer.write<uint32>(0x0000001f);
		aBuffer.write<uint32>(0x0000a395);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0x00000008);
		aBuffer.write<uint32>(0x00000008);
		aBuffer.write<uint32>(0x00006157);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint64>(0);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0x000000dc);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);

		if(client->charInfo.curExp >= client->charInfo.maxStats.exp)
		{
			client->charInfo.level++;
			increaseLevel(client);
		}
	}
	updateNpcData(GTable, curNPC);
	//*/
	/*
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000002d);
	aBuffer.write<uint32>(0xd751ebb4);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(target);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(curCombat.position.x);
	aBuffer.write<uint32>(curCombat.position.y);
	aBuffer.write<uint32>(curCombat.position.z);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0xbf659eb7);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x3ee25e4e);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000004a);
	aBuffer.write<uint32>(0x642cd3d6);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(target);
	aBuffer.write<uint16>(0x0004);
	aBuffer.write<uint32>(7);
	aBuffer.write<uint32>(0x0000a1e2);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(0x00000018);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	/*
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001e);
	aBuffer.write<uint32>(0x8a01e5a1);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint16>(0x0000);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(target);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	//*/
}

uint32 WorldServer::checkEffectSpell(GlobalTable* GTable, uint32 effectId)
{
	NPC curNPC = getNpcData(GTable, GTable->client->charInfo.combat.target);

	if(curNPC.combat.effect.size() > 0)
	{
		for(int i=0; i < curNPC.combat.effect.size(); i++)
		{
			if(effectId == curNPC.combat.effect[i].effectId)
			{
				Spells effectS = getSpellData(GTable, curNPC.combat.effect[i].effectId);
				vector<string> spellsx = String::splitString(effectS.effectSpell, "-");
				uint32 efId = atoi64(spellsx[0].c_str());
				
				time_t curTime = getCurrentTime();
				curNPC.combat.effect[i].endEffectTime = curTime + 9000;
				updateNpcData(GTable, curNPC);

				//Remove old effect (Aura)
				sendPackets::other::removeEffect(GTable, effectId, curNPC.npcId);
				
				if(effectId != efId)
					return checkEffectSpell(GTable, efId);
				else
					return effectId;
			}
		}
	}

	return effectId;
}

bool WorldServer::checkHasEffect(GlobalTable* GTable, uint32 effectId)
{
	NPC curNPC = getNpcData(GTable, GTable->client->charInfo.combat.target);

	if(curNPC.combat.effect.size() > 0)
	{
		for(int i=0; i < curNPC.combat.effect.size(); i++)
		{
			if(effectId == curNPC.combat.effect[i].effectId)
			{
				return true;
			}
		}
	}
	return false;
}

void WorldServer::HitEnemy(GlobalTable* GTable, Packet* packet)
{
	GameClient* client = GTable->client;

	uint32 unk2 = 0;
	uint32 unk3 = 0;
	uint32 unk4 = 0;
	uint32 unk5 = 0;
	uint32 unk6 = 0;
	uint32 unk7 = 0;
	uint32 unk8 = 0;
	uint32 unk9 = 0;
	uint32 unk10 = 0;
	uint32 unk11 = 0;
	uint32 unk12 = 0;
	uint32 target = 0;
	uint32 unk13 = 0;
	uint32 unk14 = 0;
	uint32 unk15 = 0;
	uint32 unk16 = 0;
	uint32 unk17 = 0;
	uint32 unk18 = 0;
	uint32 unk19 = 0;
	uint32 unk20 = 0;
	uint32 unk21 = 0;

	try
	{
		unk2 = packet->data->read<uint32>();
		unk3 = packet->data->read<uint32>();
		unk4 = packet->data->read<uint32>();
		unk5 = packet->data->read<uint32>();
		unk6 = packet->data->read<uint32>();
		unk7 = packet->data->read<uint32>();
		unk8 = packet->data->read<uint32>();
		unk9 = packet->data->read<uint32>();
		unk10 = packet->data->read<uint32>();
		unk11 = packet->data->read<uint32>();
		unk12 = packet->data->read<uint32>();
		target = packet->data->read<uint32>();
		unk13 = packet->data->read<uint32>();
		unk14 = packet->data->read<uint32>();
		unk15 = packet->data->read<uint32>();
		unk16 = packet->data->read<uint32>();
		unk17 = packet->data->read<uint32>();
		unk18 = packet->data->read<uint32>();
		unk19 = packet->data->read<uint32>();
		unk20 = packet->data->read<uint32>();
		unk21 = packet->data->read<uint32>();
	}
	catch(char* msg)
	{
		Log.Error("Crash Error at Function HitEnemy @ CobatHandler.cpp\nMsg: %s \n", msg);
	}

	NPC curCombat = getNpcData(GTable, target);

	if(target>0)
	{
		GTable->client->charInfo.combat.target = target;
		if(checkEnemyInRange(GTable) && curCombat.fraction > 0)
		{
			curCombat.combat.target = client->nClientInst;
			Log.Debug("Set target to: 0x%08x\n", client->nClientInst);
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
			aBuffer.write<uint32>(0x0000009b);
			aBuffer.write<uint32>(0x642cd3d6);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<uint16>(0x0004);
			aBuffer.write<uint32>(unk2);
			aBuffer.write<uint32>(unk3);
			aBuffer.write<uint32>(unk4);
			aBuffer.write<uint32>(unk4); //second time unk4
			aBuffer.write<uint32>(unk6);
			aBuffer.write<uint32>(unk7);
			aBuffer.write<uint32>(unk8);
			aBuffer.write<uint32>(unk9);
			aBuffer.write<uint32>(unk10);
			aBuffer.write<uint32>(unk11);
			aBuffer.write<uint32>(unk12);
			aBuffer.write<uint32>(target);
			aBuffer.write<uint32>(unk13);
			aBuffer.write<uint32>(unk14);
			aBuffer.write<uint32>(unk15);
			aBuffer.write<uint32>(unk16);
			aBuffer.write<uint32>(unk17);
			aBuffer.write<uint32>(unk18);
			aBuffer.write<uint32>(unk19);
			aBuffer.write<uint32>(unk20);
			aBuffer.write<uint32>(unk21);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(1);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(target);
			aBuffer.write<uint8>(0);
			aBuffer.write<uint32>(0x00000006);
			aBuffer.write<uint32>(0x000000c9);
			aBuffer.write<uint32>(0x00000000);
			aBuffer.write<uint32>(0x00000002);
			aBuffer.write<uint32>(0x00000960); //hit 24.00
			aBuffer.write<uint32>(0x00000002);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0x3e4f4f3c);
			aBuffer.doItAll(client->clientSocket);

			curCombat.curHealth -= 2400;

			uint32 checkHealth = curCombat.curHealth & 0xf0000000;

			if(curCombat.curHealth <= 0 || checkHealth == 0xf0000000)
			{
				client->charInfo.curExp += curCombat.getExp;
				curCombat.combat.inCombat = false;
				curCombat.combat.target = 0;

				increaseExp(client);

				aBuffer = PacketBuffer(500);
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
				aBuffer.write<uint32>(0x0000004a);
				aBuffer.write<uint32>(0x642cd3d6);	// to enemy down
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->charInfo.combat.target);
				aBuffer.write<uint16>(0x0004);
				aBuffer.write<uint32>(0x0000001f);
				aBuffer.write<uint32>(0x0000a395);
				aBuffer.write<uint32>(0);
				aBuffer.write<uint32>(0x00000008);
				aBuffer.write<uint32>(0x00000008);
				aBuffer.write<uint32>(0x00006157);
				aBuffer.write<uint64>(0);
				aBuffer.write<uint64>(0);
				aBuffer.write<uint64>(0);
				aBuffer.write<uint32>(0);
				aBuffer.write<uint32>(0x000000dc);
				aBuffer.write<uint32>(0x3e4f4f3c);
				aBuffer.doItAll(client->clientSocket);

				if(client->charInfo.curExp >= client->charInfo.maxStats.exp)
				{
					client->charInfo.level++;
					increaseLevel(client);
				}
			}
		}
		else
		{
			PacketBuffer aBuffer(500);
			aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
			aBuffer.write<uint32>(0x00000072);
			aBuffer.write<uint32>(0x642cd3d6);
			aBuffer.write<uint32>(0x0000c350);
			aBuffer.write<uint32>(client->nClientInst);
			aBuffer.write<uint16>(0x0004);
			aBuffer.write<uint32>(unk2);
			aBuffer.write<uint32>(unk3);
			aBuffer.write<uint32>(unk4);
			aBuffer.write<uint32>(unk5 + 1);
			aBuffer.write<uint32>(unk6);
			aBuffer.write<uint32>(unk7);
			aBuffer.write<uint32>(unk8);
			aBuffer.write<uint32>(unk9);
			aBuffer.write<uint32>(unk10);
			aBuffer.write<uint32>(unk11);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(unk13);
			aBuffer.write<uint32>(unk14);
			aBuffer.write<uint32>(unk15);
			aBuffer.write<uint32>(unk16);
			aBuffer.write<uint32>(unk17);
			aBuffer.write<uint32>(unk18);
			aBuffer.write<uint32>(unk19);
			aBuffer.write<uint32>(unk20);
			aBuffer.write<uint32>(unk21);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0);
			aBuffer.write<uint32>(0x3e4f4f3c);
			aBuffer.doItAll(client->clientSocket);
		}
	}
	else
	{
		PacketBuffer aBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x00000072);
		aBuffer.write<uint32>(0x642cd3d6);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.write<uint16>(0x0004);
		aBuffer.write<uint32>(unk2);
		aBuffer.write<uint32>(unk3);
		aBuffer.write<uint32>(unk4);
		aBuffer.write<uint32>(unk5 + 1);
		aBuffer.write<uint32>(unk6);
		aBuffer.write<uint32>(unk7);
		aBuffer.write<uint32>(unk8);
		aBuffer.write<uint32>(unk9);
		aBuffer.write<uint32>(unk10);
		aBuffer.write<uint32>(unk11);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(unk13);
		aBuffer.write<uint32>(unk14);
		aBuffer.write<uint32>(unk15);
		aBuffer.write<uint32>(unk16);
		aBuffer.write<uint32>(unk17);
		aBuffer.write<uint32>(unk18);
		aBuffer.write<uint32>(unk19);
		aBuffer.write<uint32>(unk20);
		aBuffer.write<uint32>(unk21);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);
	}

	updateNpcData(GTable, curCombat);
}

void WorldServer::increaseExp(GameClient* client)
{
	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000019);
	aBuffer.write<uint32>(0xf98e10b3);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x01);
	aBuffer.write<uint32>(0x00000034);
	aBuffer.write<uint32>(client->charInfo.curExp); //the complete exp from user.
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
}

void WorldServer::increaseLevel(GameClient* client, int lvl)
{

	//client->charInfo.level+=lvl;
	//change level

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000019);
	aBuffer.write<uint32>(0xf98e10b3);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x01);//01
	aBuffer.write<uint32>(0x000003c5);
	aBuffer.write<uint32>(lvl? client->charInfo.level+lvl : client->charInfo.level ); //visual level of char
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000002d);
	aBuffer.write<uint32>(0x6d314743);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(lvl? client->charInfo.level+lvl : client->charInfo.level); //physical level :)
	aBuffer.write<uint32>(client->charInfo.maxStats.exp);
	aBuffer.write<uint32>(client->charInfo.maxStats.exp);
	aBuffer.write<uint32>(0x00000154);
	aBuffer.write<uint32>(((lvl? client->charInfo.level+lvl : client->charInfo.level) - 1));
	aBuffer.write<uint32>(0x00000008);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	client->charInfo.level+=lvl;
	
	
	checkNewAbilities(client);

	//Activate lvl2
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000031);
	aBuffer.write<uint32>(0x6b6fd368);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0x00019833+client->charInfo.level);
	//aBuffer.write<uint32>(0x00019835); //0x00019833+lvl
	aBuffer.write<uint64>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//Update server stuff
	
	Database.getMaxStats(client);
	client->charInfo.InitalizeStatupStats(client);
	
	//update here max hp,stamina,mana stas for client sniff here for the packet 
	//Update stamina
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(MAXSTAMINA);
	aBuffer.write<uint32>(client->charInfo.maxStats.stamina*100);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	if(client->charInfo.maxStats.mana>0)
	{
		//mana
		aBuffer = PacketBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000001d);
		aBuffer.write<uint32>(0x96b8dc59);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(0x00000001);
		aBuffer.write<uint32>(MAXMANA);
		aBuffer.write<uint32>(client->charInfo.maxStats.mana*100);
		aBuffer.write<uint32>(0x3e4f4f3c);
		aBuffer.doItAll(client->clientSocket);
	}
	//Update Health
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(MAXHEALTH);
	aBuffer.write<uint32>(client->charInfo.maxStats.health*100);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	//Add Attributes
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(ATTRIB_CONSTITUTION);
	aBuffer.write<uint32>(client->charInfo.PlayerAttributes.Constitution*10);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(ATTRIB_DEXTERITY);
	aBuffer.write<uint32>(client->charInfo.PlayerAttributes.Dexterity*10);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(ATTRIB_INTELLIGENCE);
	aBuffer.write<uint32>(client->charInfo.PlayerAttributes.Intelligence*10);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(ATTRIB_STRENGTH);
	aBuffer.write<uint32>(client->charInfo.PlayerAttributes.Strength*10);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	aBuffer = PacketBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(ATTRIB_WISDOM);
	aBuffer.write<uint32>(client->charInfo.PlayerAttributes.Wisdom*10);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

}

bool WorldServer::checkEnemyInRange(GlobalTable* GTable)
{
	GameClient* client = GTable->client;
	NPC combat_npc = getNpcData(GTable, client->charInfo.combat.target);
	if(combat_npc.position.x >= (client->charInfo.position.x - 0x00010000) && combat_npc.position.x <= (client->charInfo.position.x + 0x00010000))
	{
		if(combat_npc.position.y >= (client->charInfo.position.y - 0x00010000) && combat_npc.position.y <= (client->charInfo.position.y + 0x00010000))
		{
			if(combat_npc.position.z >= (client->charInfo.position.z - 0x00010000) && combat_npc.position.z <= (client->charInfo.position.z + 0x00010000))
			{
				return true;
			}
			else
			{
				//Don't work correctly
				//enemyRunToPlayer(client);
				return false;
			}
		}
		else
		{
			//Don't work correctly
			//enemyRunToPlayer(client);
			return false;
		}
	}
	else
	{
		//Don't work correctly
		//enemyRunToPlayer(client);
		return false;
	}
}

void WorldServer::enemyRunToPlayer(GlobalTable* GTable)
{
	GameClient* client = GTable->client;
	/*
	DON'T WORK CORRECTLY -> DISABLED

	first need to find how to create the speed-value.
	*/

	uint32 speed = 0x41518ebd; //1095863997 ~ 3 seconds to player
	//0x41eb2856 ~ 5 seconds
	//speed = 0x42518ebd; // 1112641213   ~ 8 seconds to player
	//0x431e5b88 ~24 seconds
	/*
	3 seconds = 0x41518ebd
	0x999999 = 10066329 // + 2seconds   // per second 5033164
	5 seconds = 0x41eb2856
	0x666667 =  6710887 // + 3seconds   // per second 2236962
	8 seconds = 0x42518ebd  // 1112641213
	0xCCCCCB = 13421771 // + 16 seconds // per second 838860
	24 seconds = 0x431e5b88 // 1126062984
	//*/
	//speed = 0x425E5B89; //it is ~ 9 seconds // 1113480073

	//take 8 seconds as speed
	speed = 0x42518ebd;

	NPC combat_npc = getNpcData(GTable, client->charInfo.combat.target);
	NPCMove CalcMove;
	CalcMove.range = combat_npc.position;

	if(!combat_npc.move.isMoving)
	{
#ifdef WINDOWS
		SYSTEMTIME now;
		GetSystemTime(&now);
		uint32 curTime = (((now.wSecond / 60)*100)* 1000) + now.wMilliseconds;
#else
		timeval now;
		gettimeofday(&now, 0);
		uint32 curTime = (((now.tv_sec / 60)*100)* 1000) + now.tv_usec;
#endif

		combat_npc.move.milliseconds = curTime;
		combat_npc.move.range = setRange(client, combat_npc);
		Log.Notice("Range ( 0x%08x - 0x%08x - 0x%08x)\nSpeed %u - 0x%08x\n", combat_npc.move.range.x, combat_npc.move.range.y, combat_npc.move.range.z,
			speed, speed);
		ldouble test = combat_npc.position.distance(client->charInfo.position);
		Log.Notice("Way: %f\n", test);
		combat_npc.move.isMoving = true;
	}
	else
	{
		//todo calculate way by time
		CalcMove.range = calculateWay(client, combat_npc);
		if(CalcMove.range.x == client->charInfo.position.x && CalcMove.range.y == client->charInfo.position.y && CalcMove.range.z == client->charInfo.position.z)
		{
			combat_npc.move.isMoving = false;
			combat_npc.position = CalcMove.range;
		}
	}

	PacketBuffer aBuffer(5000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000005c);
	aBuffer.write<uint32>(0x96c46740);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint16>(0);
	//aBuffer.write<uint32>(combat_npc.position.x);
	//aBuffer.write<uint32>(combat_npc.position.y);
	//aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint32>(CalcMove.range.x);
	aBuffer.write<uint32>(CalcMove.range.y);
	aBuffer.write<uint32>(CalcMove.range.z);
	aBuffer.write<uint16>(0x0401);
	aBuffer.write<uint32>(0x00020101);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000002);
	//aBuffer.write<uint32>(combat_npc.position.x);
	//aBuffer.write<uint32>(combat_npc.position.y);
	//aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint32>(CalcMove.range.x);
	aBuffer.write<uint32>(CalcMove.range.y);
	aBuffer.write<uint32>(CalcMove.range.z);
	//aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.x, combat_npc.position.x));
	//aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.y, combat_npc.position.y));
	//aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.z, combat_npc.position.z));
	aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.x, CalcMove.range.x));
	aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.y, CalcMove.range.y));
	aBuffer.write<uint32>(getCoordToPlayer(client->charInfo.position.z, CalcMove.range.z));
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(speed); //i think that is something with speed
	//aBuffer.write<uint32>(0x40d08490);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	//aBuffer.write<uint32>(combat_npc.position.x);
	//aBuffer.write<uint32>(combat_npc.position.y);
	//aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint32>(CalcMove.range.x);
	aBuffer.write<uint32>(CalcMove.range.y);
	aBuffer.write<uint32>(CalcMove.range.z);
	aBuffer.write<uint16>(0x0001);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	updateNpcData(GTable, combat_npc);
}

uint32 WorldServer::getCoordToPlayer(uint32 player, uint32 npc)
{
	if(player > npc)
	{
		return (player - 0x00010000);
	}
	else if(player < npc)
	{
		return (player + 0x00010000);
	}
	else
	{
		return player;
	}
}

NPC WorldServer::getNpcData(GlobalTable* GTable, uint32 npcId)
{
	NPC dummy;
	for(uint32 i = 0; i < GTable->NPCS.size(); i++)
	{
		if(GTable->NPCS[i].npcId == npcId)
		{
			return GTable->NPCS[i];
		}
	}
	return dummy;
}

Spells WorldServer::getSpellData(GlobalTable* GTable, uint32 spellId)
{
	Spells dummy;
	for(uint32 i = 0; i < GTable->SPELLS.size(); i++)
	{
		if(GTable->SPELLS[i].id == spellId)
		{
			return GTable->SPELLS[i];
		}
	}
	return dummy;
}

void WorldServer::updateNpcData(GlobalTable* GTable, NPC curNpc)
{
	for(uint32 i = 0; i < GTable->NPCS.size(); i++)
	{
		if(GTable->NPCS[i].npcId == curNpc.npcId)
		{
			GTable->NPCS[i] = curNpc;
		}
	}
}

Vector3D WorldServer::setRange(GameClient* client, NPC curNpc)
{
	uint32 x, y, z;
	if(client->charInfo.position.x > curNpc.position.x)
	{
		x = client->charInfo.position.x - curNpc.position.x;
	}
	else
	{
		x = curNpc.position.x - client->charInfo.position.x;
	}

	if(client->charInfo.position.y > curNpc.position.y)
	{
		y = client->charInfo.position.y - curNpc.position.y;
	}
	else
	{
		y = curNpc.position.y - client->charInfo.position.y;
	}

	if(client->charInfo.position.z > curNpc.position.z)
	{
		z = client->charInfo.position.z - curNpc.position.z;
	}
	else
	{
		z = curNpc.position.z - client->charInfo.position.z;
	}

	return Vector3D(x, y, z);
}

Vector3D WorldServer::calculateWay(GameClient* client, NPC curNpc)
{
#ifdef WINDOWS
	SYSTEMTIME now;
	GetSystemTime(&now);
	uint32 curTime = (((now.wSecond / 60)*100)* 1000) + now.wMilliseconds;
#else
	timeval now;
	gettimeofday(&now, 0);
	uint32 curTime = (((now.tv_sec / 60)*100)* 1000) + now.tv_usec;
#endif

	int32 dif = curTime - curNpc.move.milliseconds;
	Log.Notice("Current time: %i\nStart time: %i\nDifferenz: %i\n", curTime, curNpc.move.milliseconds, dif);
	int32 x, y, z;
	//8 = speed in seconds
	if( dif < (8 * 1000))
	{
		NPCMove CalcMove;
		CalcMove.range = getCurrentPosition(curNpc.move.range, dif, 8);
		if(client->charInfo.position.x > curNpc.position.x)
		{
			x = curNpc.position.x + CalcMove.range.x;
			if(x > client->charInfo.position.x)
			{
				x = client->charInfo.position.x;
			}
		}
		else
		{
			x = curNpc.position.x - CalcMove.range.x;
			if(x < client->charInfo.position.x)
			{
				x = client->charInfo.position.x;
			}
		}
		if(client->charInfo.position.y > curNpc.position.y)
		{
			y = curNpc.position.y + CalcMove.range.y;
			if(y > client->charInfo.position.y)
			{
				y = client->charInfo.position.y;
			}
		}
		else
		{
			y = curNpc.position.y - CalcMove.range.y;
			if(y < client->charInfo.position.y)
			{
				y = client->charInfo.position.y;
			}
		}
		if(client->charInfo.position.z > curNpc.position.z)
		{
			z = curNpc.position.z + CalcMove.range.z;
			if(z > client->charInfo.position.z)
			{
				z = client->charInfo.position.z;
			}
		}
		else
		{
			z = curNpc.position.z - CalcMove.range.z;
			if(z < client->charInfo.position.z)
			{
				z = client->charInfo.position.z;
			}
		}
	}
	else
	{
		x = client->charInfo.position.x;
		y = client->charInfo.position.y;
		z = client->charInfo.position.z;
	}

	Log.Notice("Calculating Vector: (0x%08x - 0x%08x - 0x%08x)\n", x, y, z);
	return Vector3D(x, y, z);
}

Vector3D WorldServer::getCurrentPosition(Vector3D range, uint32 diff, uint32 speed)
{
	uint32 x = (range.x / (speed * 100)) * diff;
	uint32 y = (range.y / (speed * 100)) * diff;
	uint32 z = (range.z / (speed * 100)) * diff;

	return Vector3D(x, y, z);
}

bool WorldServer::checkInRange(Vector3D target, Vector3D npc)
{
	if(target.distance(npc) < 100000)
		return true;

	return false;
}

void WorldServer::npcHitPlayer(GlobalTable* GTable, uint32 npcId)
{
	GameClient* client = GTable->client;
	NPC curCombat = getNpcData(GTable, npcId);

	PacketBuffer aBuffer(5000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x00000097);
	aBuffer.write<uint32>(0x642cd3d6);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(curCombat.npcId);
	aBuffer.write<uint16>(0x0004);
	aBuffer.write<uint32>(0x0000000f);
	aBuffer.write<uint32>(0x0000ab21);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000008);
	aBuffer.write<uint32>(0x00000018);
	aBuffer.write<uint32>(0x000070ad);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint32>(0x0000003c);
	aBuffer.write<uint32>(0x00000034);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0);
	aBuffer.write<uint32>(0x00000006);
	aBuffer.write<uint32>(0x000000c9);
	aBuffer.write<uint32>(0x00000000); //
	aBuffer.write<uint32>(0x00000002); //attack speed?
	aBuffer.write<uint32>(0x00001388); //hit 1.00 /50.00
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	client->charInfo.hp -=50; //testing healthregen

	updateNpcData(GTable, curCombat);
}

void WorldServer::moveEnemyToPlayer(GlobalTable * GTable, uint32 target)
{
	GameClient* client = GTable->client;
	NPC combat_npc = getNpcData(GTable, target);
	if(!combat_npc.move.isMoving)
	{
	combat_npc.move.isMoving = true;
	NPCMove CalcMove;
	CalcMove.range = combat_npc.position;
	uint32 speed = 0x42000000; //~6 seconds
	speed = 0x40000000; //~0.5 seconds
	speed = 0x41000000; //~1.2 seconds
	PacketBuffer aBuffer(20000);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000005c);
	aBuffer.write<uint32>(0x96c46740);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->charInfo.combat.target);
	aBuffer.write<uint16>(0);
	aBuffer.write<uint32>(combat_npc.position.x);
	aBuffer.write<uint32>(combat_npc.position.y);
	aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint16>(0x0401);	
	aBuffer.write<uint32>(0x00020101);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000002);
	aBuffer.write<uint32>(combat_npc.position.x);
	aBuffer.write<uint32>(combat_npc.position.y);
	aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint32>(client->charInfo.position.x);
	aBuffer.write<uint32>(client->charInfo.position.y);
	aBuffer.write<uint32>(client->charInfo.position.z);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(speed); //i think that is something with speed
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(0);
	aBuffer.write<uint32>(combat_npc.position.x);
	aBuffer.write<uint32>(combat_npc.position.y);
	aBuffer.write<uint32>(combat_npc.position.z);
	aBuffer.write<uint16>(0x0001);
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	
	updateNpcData(GTable, combat_npc);
	}
}

time_t WorldServer::getCurrentTime()
{
	time_t curTime;
	time(&curTime);

	#ifdef WINDOWS
		SYSTEMTIME now;
		GetSystemTime(&now);
		curTime = (curTime * 1000) + now.wMilliseconds;
	#else
		timeval now;
		gettimeofday(&now, 0);
		curTime = (curTime * 1000) + now.tv_usec;
	#endif

	return curTime;
}