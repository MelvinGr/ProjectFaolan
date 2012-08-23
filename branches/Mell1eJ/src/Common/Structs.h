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

#ifndef STRUCTS_H
#define STRUCTS_H

#include <string>

#include "Common.h"
#include "Vector3D.h"
#include "Functions.h"

class FAOLANEXPORTED RealmInfo
{
public:
	uint32 realmID;
	std::string realmName;
	uint16 realmType; // 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask
	uint32 loadStatus; // 0 = medium load 1 = medium load 2 = heavy load 3 = full

	std::string universeAgentIPAddress;
	uint32 universeAgentPort;
	bool universeAgentActive, universeAgentInitializing;
	
	std::string playerAgentIPAddress;
	uint32 playerAgentPort;
	bool playerAgentActive, playerAgentInitializing;

	std::string worldServerIPAddress;
	uint32 worldServerPort;
	bool worldServerActive, worldServerInitializing;

	std::string agentServerIPAddress;
	uint32 agentServerPort;
	bool agentServerActive, agentServerInitializing;

	std::string csPlayerAgentIPAddress;
	uint32 csPlayerAgentPort;
	bool csPlayerAgentActive, csPlayerAgentInitializing;

	RealmInfo()
		: realmID(0), realmType(0), loadStatus(0),
			universeAgentPort(0), universeAgentActive(0), universeAgentInitializing(0),
			playerAgentPort(0), playerAgentActive(0), playerAgentInitializing(0),
			csPlayerAgentPort(0), csPlayerAgentActive(false), csPlayerAgentInitializing(false),
			agentServerPort(0), agentServerActive(false), agentServerInitializing(false),
			worldServerPort(0), worldServerActive(false), worldServerInitializing(false)
	{
		//
	}
};

class FAOLANEXPORTED CharacterInfo
{
public:
	uint32 characterID;
	uint32 accountID;
	std::string name;
	uint32 race;
	uint32 Class; 
	uint32 level;
	uint32 sex;
	uint32 realmID;
	std::string lastConnection;
	uint32 map;
	uint32 headmesh;
	uint32 size;
	uint32 voice;
	std::string language;
	uint32 lbinprv;

	Vector3D position;
	Vector3D rotation;
};

class FAOLANEXPORTED AccountInfo
{
public:
	uint32 accountID;
	std::string username;
	std::string mail;
	uint32 kind;
	uint32 characterCount;
	uint32 cookie;
};

#endif