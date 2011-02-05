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

#include "Packet.h"

Packet::Packet(PacketBuffer* pBuffer)
{
	packetBuffer = pBuffer;

	length = packetBuffer->read<uint32>();
	crc32 = packetBuffer->read<uint32>();
	sender = packetBuffer->read<string>();
	unknown1 = packetBuffer->read<uint32>(); 
	unknown2 = packetBuffer->read<uint32>();
	receiver = packetBuffer->read<string>();
	userID = packetBuffer->read<uint32>();
	unknown4 = packetBuffer->read<uint32>();
	opcode = packetBuffer->read<uint32>();

	uint32 dataLength = length - HeaderLength();
	data = new PacketBuffer(packetBuffer->readArray(dataLength), dataLength);
}

uint32 Packet::HeaderLength()
{
	return sizeof(uint16) * 2 + sizeof(uint32) * 6 + sender.length() + receiver.length();
}

void Packet::drop()
{
	delete data;
}