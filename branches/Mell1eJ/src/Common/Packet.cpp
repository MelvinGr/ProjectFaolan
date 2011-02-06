/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

#include "Packet.h"

#include <sstream>

Packet::Packet(PacketBuffer* buf) : buffer(buf)
{
	length = buffer->read<uint32>();
	crc32 = buffer->read<uint32>();
	sender = buffer->read<string>();
	unknown1 = buffer->read<uint32>(); 
	unknown2 = buffer->read<uint32>();
	receiver = buffer->read<string>();
	userID = buffer->read<uint32>();
	unknown4 = buffer->read<uint32>();
	opcode = buffer->read<uint32>();
}

inline uint32 Packet::HeaderLength()
{
	return sizeof(uint16) * 2 + sizeof(uint32) * 6 + sender.length() + receiver.length();
}

string Packet::toString()
{
	stringstream ss;
	ss <<
		"length: "		<< length << "\n" <<
		"crc32: "		<< crc32 << "\n" <<
		"sender: "		<< sender << "\n" <<
		"unknown1: "	<< unknown1 << "\n" <<
		"unknown2: "	<< unknown2 << "\n" <<
		"receiver: "	<< receiver << "\n" <<
		"userID: "		<< userID << "\n" <<
		"unknown4: "	<< unknown4 << "\n" <<
		"opcode: "		<< opcode << "\n" <<
		"data Length: "	<< length - HeaderLength() << "\n";

	return ss.str();
}