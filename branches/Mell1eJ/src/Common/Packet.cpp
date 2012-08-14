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

#include "Packet.h"

using namespace std;

Packet::Packet(PacketBuffer &packetBuffer) 
	: senderInt(0), receiverInt(0), headerData(0), data(0)
{
	length = packetBuffer.read<uint32>();
	crc32 = packetBuffer.read<uint32>();
	headersize = packetBuffer.read<uint32>();

	sender = packetBuffer.read<uint8>();
	senderLength = packetBuffer.read<uint8>();

	if(senderLength <= 0)
		printf("Error at senderlength\n");

	senderInt = new PacketBuffer(packetBuffer.read(senderLength), senderLength);

	receiver = packetBuffer.read<uint8>();
	receiverLength = packetBuffer.read<uint8>();

	if(receiverLength <= 0)
		printf("Error at receiverlength\n");

	receiverInt = new PacketBuffer(packetBuffer.read(receiverLength), receiverLength);

	uint32 checkHeaderLength = headersize - (sizeof(uint32) + senderLength + receiverLength);
	if(checkHeaderLength == sizeof(uint16))
		opcode = packetBuffer.read<uint16>();
	else if(checkHeaderLength == sizeof(uint32))
		opcode = packetBuffer.read<uint32>();
	else if(checkHeaderLength > sizeof(uint32))
	{
		headerData = new PacketBuffer(packetBuffer.read(checkHeaderLength - sizeof(uint32)), checkHeaderLength - sizeof(uint32));
		opcode = packetBuffer.read<uint32>();
	}
	else
	{
		printf("ERROR at Packet.cpp (Headerstruct)\n");
	}

	uint32 dataLength = length - (headersize + sizeof(uint32) * 2);
	if(dataLength > 0)
		data = new PacketBuffer(packetBuffer.read(dataLength), dataLength);
	else
		printf("ERROR at Packet.cpp: dtaLength: %i\n", dataLength);
}

Packet::~Packet()
{
	if(senderInt)
		delete senderInt;
	if(receiverInt)
		delete receiverInt;
	if(headerData)
		delete headerData;
	if(data)
		delete data;
}

uint32 Packet::HeaderLength()
{
	return (headersize + sizeof(uint32) * 2);
}