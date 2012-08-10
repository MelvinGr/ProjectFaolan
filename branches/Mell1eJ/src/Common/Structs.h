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

struct RealmInfo
{
	uint32 realmID;
	std::string realmName;
	uint16 realmType; // 0,1 = PvE | 256,257 = PvP | 1081 = PvP by faction, probably a bitmask

	uint32 onlineStatus; //2 = online, other num = offline
	uint32 loadStatus; // 0 = medium load 1 = medium load 2 = heavy load 3 = full

	std::string csPlayerAgentIPAddress;
	uint32 csPlayerAgentPort;

	std::string agentServerIPAddress;
	uint32 agentServerPort;

	std::string worldServerIPAddress;
	uint32 worldServerPort;
};

struct AccountInfo
{
	uint32 accountID;
	std::string username;
	std::string mail;
	uint32 kind;
	uint32 characterCount;
	uint32 cookie;
};

#endif