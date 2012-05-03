/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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

#ifndef SETTINGSCLASS_H
#define SETTINGSCLASS_H

#include <iostream>
#include <string>
#include <vector>

#include "Common.h"
#include "Structs.h"
#include "Singleton.h"
#include "ConfigParser/Config.h"

using namespace std;

#define Settings SettingsClass::getSingleton()
class SettingsClass : public Singleton<SettingsClass>
{
	Config config;

public:
	string mysqlAccount;
	string mysqlPassword;
	string mysqlAddress;
	uint32 mysqlPort;
	string mysqlDatabase;

	string worldServerIPAddress;
	uint32 worldServerPort;

	string agentServerIPAddress;
	uint32 agentServerPort;

	string csPlayerAgentIPAddress;
	uint32 csPlayerAgentPort;

	string playerAgentIPAddress;
	uint32 playerAgentPort;

	uint32 universeAgentPort;

	string realmName;

	uint32 characterSlots;
	uint32 characterSlotsTrial;
	string clientVersion;

	vector<RealmInfo*> realms;

	bool playIntro;

	bool load(string settingsFile, int8* envp[]);
	SettingsClass(){}
};

#endif
