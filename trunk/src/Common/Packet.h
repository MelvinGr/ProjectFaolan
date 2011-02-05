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
gcc */

#ifndef PACKET_H
#define PACKET_H

#include <stdio.h>
#include <string>
#include <zlib.h>

#include "Common.h"
#include "PacketBuffer.h"

using namespace std;

struct Packet
{
	PacketBuffer* packetBuffer;

	uint32 length;
	uint32 crc32;
	string sender;
	uint32 unknown1;
	uint32 unknown2;
	string receiver;
	uint32 userID;
	uint32 unknown4;
	uint32 opcode;
	PacketBuffer* data; // use this one for reading data!!

	Packet(PacketBuffer* pBuffer);
	uint32 HeaderLength();
	void drop();
};
#endif