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

#include "Packet.h"

Packet::Packet(PacketBuffer* pBuffer)
{
	packetBuffer = pBuffer;
	length = packetBuffer->read<uint32>();
	crc32 = packetBuffer->read<uint32>();
	headersize = packetBuffer->read<uint32>(); //size of header
	sender = packetBuffer->read<uint8>();
	slength = packetBuffer->read<uint8>();
	if(slength<=0)
		printf("Error at senderlength\n");
	senderInt = new PacketBuffer(packetBuffer->readArray(slength), slength);
	receiver = packetBuffer->read<uint8>();
	rlength = packetBuffer->read<uint8>();
	if(rlength<=0)
		printf("Error at receiverlength\n");
	receiverInt = new PacketBuffer(packetBuffer->readArray(rlength), rlength);

	uint32 checkHeaderLength = headersize - (4 + slength + rlength);

	if(checkHeaderLength == 2)
	{
		opcode = packetBuffer->read<uint16>();
	}
	else if(checkHeaderLength == 4)
	{
		opcode = packetBuffer->read<uint32>();
	}
	else if(checkHeaderLength > 4)
	{
		headerData = new PacketBuffer(packetBuffer->readArray((checkHeaderLength - 4)), (checkHeaderLength - 4));
		opcode = packetBuffer->read<uint32>();
	}
	else if(checkHeaderLength == 3)
	{
		opcode = packetBuffer->read<uint16>();
		headerData = new PacketBuffer(packetBuffer->readArray((checkHeaderLength - 2)), (checkHeaderLength - 2));
	}
	else
	{
		printf("ERROR at Packet.cpp (Headerstruct)\nPacketLength: 0x%08x\nheaderSize: 0x%08x\n", length, headersize);
	}

	uint32 dataLength = length - (headersize + 8);
	if(dataLength > 0)
		data = new PacketBuffer(packetBuffer->readArray(dataLength), dataLength);
	else
	{
		data = new PacketBuffer(0);
	}
}

uint32 Packet::HeaderLength()
{
	return (headersize + 8);
}

void Packet::drop()
{
	delete senderInt;
	delete receiverInt;
	delete headerData;
	delete data;
}