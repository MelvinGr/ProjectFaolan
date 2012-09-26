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

#include "Settings.h"

CreateFileSingleton(SettingsClass);
bool SettingsClass::load(string settingsFile, int8* envp[]) 
{
	if(!FileExists(settingsFile.c_str()))
	{
		Log.Error("Could not find Settings File: %s!\n", settingsFile.c_str());
		return false;
	}

	config = Config(settingsFile, envp);

	mysqlAccount = config.pString("mysqlAccount");
	mysqlPassword = config.pString("mysqlPassword");
	mysqlAddress = config.pString("mysqlAddress");
	mysqlPort = config.pInt("mysqlPort");
	mysqlDatabase = config.pString("mysqlDatabase");

	universeAgentPort = config.pInt("universeAgentPort");

	playerAgentIPAddress = config.pString("playerAgentIPAddress");
	playerAgentPort = config.pInt("playerAgentPort");

	csPlayerAgentIPAddress = config.pString("csPlayerAgentIPAddress");
	csPlayerAgentPort = config.pInt("csPlayerAgentPort");

	agentServerIPAddress = config.pString("agentServerIPAddress");
	agentServerPort = config.pInt("agentServerPort");

	worldServerIPAddress = config.pString("worldServerIPAddress");
	worldServerPort = config.pInt("worldServerPort");

	realmName = config.pString("realmName");

	characterSlots = config.pInt("characterSlots");
	clientVersion = config.pString("clientVersion");

	playIntro = config.pBool("playIntro");
	/*
	RealmInfo* realm1 = new RealmInfo();
	realm1->realmID = 1;
	realm1->onlineStatus = 2; //2 = online, other num = offline
	realm1->realmName = realmName;
	realm1->loadStatus = 0; // 0 = medium load 1 = medium load 2 = heavy load 3 = full
	realm1->realmType = 0; // 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
	realm1->csPlayerAgentIPAddress = csPlayerAgentIPAddress;
	realm1->csPlayerAgentPort = csPlayerAgentPort;
	realm1->agentServerIPAddress = agentServerIPAddress;
	realm1->agentServerPort = agentServerPort;
	realm1->worldServerIPAddress = worldServerIPAddress;
	realm1->worldServerPort = worldServerPort;
	realms.push_back(realm1);
	//*/

	return true;
}