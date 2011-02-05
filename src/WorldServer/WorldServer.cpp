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

#include "WorldServer.h"

vector<GameClient*>* clientList = new vector<GameClient*>();

void WorldServer::HandleNpcCombat(GlobalTable* GTable)
{
	GameClient* client = GTable->client;
	time_t actTime;

	double combatTimeDiff = 0;
	double auraTimeDiff = 0;
	time_t auraLastCheck = time(&actTime);

	while(client->isConnected)
	{
		time(&actTime);
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

		//Combat
		for(uint32 i = 0; i < GTable->NPCS.size(); i++)
		{
			if(GTable->NPCS[i].combat.target == client->nClientInst && GTable->NPCS[i].fraction > 0 && GTable->NPCS[i].mapId == client->charInfo.map)
			{
				GTable->NPCS[i].combat.attackSpeed = 4.00;
				
				combatTimeDiff = difftime(actTime, GTable->NPCS[i].combat.lastcheck);
				if(checkInRange(client->charInfo.position, GTable->NPCS[i].position))
				{
					if(combatTimeDiff >= GTable->NPCS[i].combat.attackSpeed)
					{
						GTable->NPCS[i].combat.lastcheck = actTime;
						npcHitPlayer(GTable, GTable->NPCS[i].npcId);
					}
				}
				else
				{
					//Enemy run to npc
					//testing
					//moveEnemyToPlayer(client, client->datastoreNpcs[i].npcId);
				}
			}
			if(GTable->NPCS[i].combat.effect.size() > 0)
			{
				for(int x = 0; x < GTable->NPCS[i].combat.effect.size(); x++)
				{
					if(curTime >= GTable->NPCS[i].combat.effect[x].endEffectTime)
					{
						sendPackets::other::removeEffect(GTable, GTable->NPCS[i].combat.effect[x].effectId, GTable->NPCS[i].npcId);
						GTable->NPCS[i].combat.effect.erase(GTable->NPCS[i].combat.effect.begin() + x, GTable->NPCS[i].combat.effect.begin() + x + 1);
					}
				}
			}
		}

		//Auras
		auraTimeDiff = difftime(actTime, auraLastCheck);
		if(auraTimeDiff > 1.200)
		{
			auraLastCheck = actTime;
			client->charInfo.Update(GTable->client);
		}

		//Spell
		if(client->charInfo.combat.spellId > 0)
		{
			if(client->charInfo.combat.activateSpell <= curTime)
			{
				sendSpellData(GTable);
				GTable->client->charInfo.combat.spellId = 0;
			}
		}
	}
}

void WorldServer::HandleClient(GlobalTable* GTable)
{
	void* socket = GTable->socket;
	GameClient* client = new GameClient(*((uint32*)socket));
	clientList->push_back(client);
	GTable->client = client;
	//Need to set 0 later set to char current exp
	GTable->client->charInfo.curExp = 0;
	GTable->client->charInfo.hp = 1;
	GTable->client->charInfo.stamina = 1;
	GTable->client->charInfo.mana = 1;
	//create Thread for Combat and regenerations
	#ifdef WINDOWS
			CreateThread(0, 0, (LPTHREAD_START_ROUTINE)WorldServer::HandleNpcCombat, GTable, 0, 0);
	#else
			pthread_create(0, 0, (void*(*)(void*))WorldServer::HandleNpcCombat, GTable);
	#endif

	client->counter = 2;

	Timer timer;

	while(client->isConnected)
	{
		Packet* packet = client->getNextPacket(true);
		if(packet)
		{
			if(packet->receiver == "GameAgent")
				GameAgentHandler(packet, GTable, clientList);
			else if(packet->receiver == "GameCharAgent")
				GameCharAgentHandler(packet, GTable);
			else if(packet->receiver == "GameCharAgentProject")
				GameCharAgentProjectHandler(packet, client);

			delete packet;
		}
	}

	Database.updateCharacter(&client->charInfo);	
	remove(clientList->begin(), clientList->end(), client); 
	delete client;
}

int32 main(int32 argc, int8* argv[], int8* envp[])
{
#ifdef WINDOWS
	SetConsoleTitle("Project Faolan - WorldServer");
#endif

	Log.Start("WorldServer.txt");

	if(!Settings.load("./FaolanConfig.cnf", envp)) 
		return -9;

	Log.Notice("%s\n%s\n", String::faolanBanner().c_str(), String::serverInfoStr().c_str());

	GlobalTable GTable;
	Networking network(Settings.worldServerPort);

	//Loading Global Table with values;
	Log.Success("\nLoading Global Table\n");
	int32 result = network.initDB();

	if(result == 0)
	{
		//Loading Global Table with values;
		Database.loadGlobalSpells(&GTable.SPELLS);
		Database.ladGlobalNpcs(&GTable.NPCS);

		result = network.start();
		if(result == 0) 
		{
			while(true)
			{
				SOCKET client = network.AcceptClient();
				GTable.socket = &client;
				#ifdef WINDOWS
					CreateThread(0, 0, (LPTHREAD_START_ROUTINE)WorldServer::HandleClient, &GTable, 0, 0);
				#else
					pthread_create(0, 0, (void*(*)(void*))WorldServer::HandleClient, &GTable);
				#endif
			}
		}
	}
	else Log.Error("Error while setting up the Networking, errorcode: %u!\n", result);

	return result;
}