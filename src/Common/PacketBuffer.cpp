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

#include "PacketBuffer.h"

void PacketBuffer::writeArray(uint8* data, uint32 length)
{
	try
	{
		assert((offset + length) <= maxLength);
		for(uint32 i = 0; i < length; i++)
			write<uint8>(data[i]);
	}
	catch(char* msg)
	{
		printf("Error at writing Array @ PacketBuffer.cpp\nErrorMsg: %s \n", msg);
	}
}

uint8* PacketBuffer::readArray(uint32 length)
{
	try
	{
		assert((offset + length) <= bufferLength);
		uint8* ret = new uint8[length];
		for(uint32 i = 0; i < length; i++) 
			ret[i] = read<uint8>();

		return ret;
	}
	catch(char* msg)
	{
		printf("Error at reading Array @ PacketBuffer.cpp\nErrorMsg: %s \n", msg);
		return 0;
	}
}

void PacketBuffer::writeHeader(string sender, string receiver, uint32 unknown1, uint32 unknown2, uint32 user, uint32 unknown4, uint32 opcode)
{
	write<uint32>(0); // Write empty length
	write<uint32>(0); // write empty crc32
	write<string>(sender); // write sender
	write<uint32>(unknown1); // write Unknown 1
	write<uint32>(unknown2); // write Unknown 2
	write<string>(receiver); // write receiver
	write<uint32>(user); // write userID
	write<uint32>(unknown4); // write Unknown 4
	write<uint32>(opcode); // write opcode
}

void PacketBuffer::finalize()
{
	uint32 packetLength = bufferLength - sizeof(uint32);
	SwapByte::Swap<uint32>(packetLength);
	memcpy(&buffer[0], reinterpret_cast<uint8*>(&packetLength), sizeof(int32));

	uint32 hash = CRC32::CalculateCRC32((int8*)buffer, bufferLength);
	SwapByte::Swap<uint32>(hash);
	memcpy(&buffer[4], reinterpret_cast<uint8*>(&hash), sizeof(int32));
}

void PacketBuffer::finalizeAgentServer()
{
	uint16 packetLength = bufferLength - sizeof(uint16) * 2;
	SwapByte::Swap<uint16>(packetLength);
	memcpy(&buffer[2], reinterpret_cast<uint8*>(&packetLength), sizeof(uint16));
}

void PacketBuffer::doItAll(SOCKET client)
{
	finalize();
	send(client, (int8*)buffer, bufferLength, 0);
	drop();
}

void PacketBuffer::doItAllAgentServer(SOCKET client)
{
	finalizeAgentServer();
	send(client, (int8*)buffer, bufferLength, 0);
	drop();
}