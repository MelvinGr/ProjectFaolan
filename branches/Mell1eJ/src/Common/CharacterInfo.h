/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful, 
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef CHARACTERINFO_H
#define CHARACTERINFO_H

#include "Common.h"

#include <string>

#include "Vector3D.h"
#include "Structs.h"

class CharacterInfo
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

#endif