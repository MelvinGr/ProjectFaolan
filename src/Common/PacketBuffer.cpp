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

#include "PacketBuffer.h"

string PacketBuffer::readString()
{
	if((offset + sizeof(int8)) <= maxLength)
	{
		uint8 stringLength = read<uint8>();
		string data(&buffer[offset], &buffer[offset + stringLength]);
	
		offset += stringLength;
		return data;
	}
}
void PacketBuffer::writeString(string data)
{
	assert((offset + sizeof(int8)) <= maxLength);
	write<uint8>(data.size());
	
	assert((offset + data.size()) <= maxLength);
	writeArray((uint8*)data.c_str(), data.size());
}

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

void PacketBuffer::setHeader(uint8* sender, uint8 sl, uint8* receiver, uint8 rl, uint8* headerData, uint8 hl, uint32 opcode, bool minOpcode)
{
	write<uint32>(0); // Write empty length
	write<uint32>(0); // write empty crc32

	//Write Headerlength
	write<uint32>(sl + rl + hl + 2 + 2 + 2);

	//Write sender
	write<uint8>(0x0a);
	write<uint8>(sl);
	writeArray(sender, sl);

	//Write receiver
	write<uint8>(0x12);
	write<uint8>(rl);
	writeArray(receiver, rl);
	
	//write opcode
	write<uint16>(opcode);
	
	//Write headerdata
	if(hl > 0)
		writeArray(headerData, hl);
}

/*
void PacketBuffer::setHeader(uint8* sender, uint8 sl, uint8* receiver, uint8 rl, uint8* headerData, uint8 hl, uint32 opcode, bool minOpcode)
{
	write<uint32>(0); // Write empty length
	write<uint32>(0); // write empty crc32

	//Write Headerlength
	if(minOpcode)
		write<uint32>(sl + rl + hl + 2 + 2 + 2);
	else
		write<uint32>(sl + rl + hl + 2 + 2 + 4);

	//Write sender
	write<uint8>(0x0a);
	write<uint8>(sl);
	writeArray(sender, sl);

	//Write receiver
	write<uint8>(0x12);
	write<uint8>(rl);
	writeArray(receiver, rl);

	//Write headerdata
	if(hl > 0)
		writeArray(headerData, hl);
	
	//write opcode
	if(minOpcode)
		write<uint16>(opcode);
	else
		write<uint32>(opcode);
}
//*/
/*
void PacketBuffer::writeHeader(uint8 sender[], uint8 sl, uint8 receiver[], uint8 rl, uint8 headerData[], uint8 hl, uint32 opcode, bool minOpcode)
{
	write<uint32>(0); // Write empty length
	write<uint32>(0); // write empty crc32
	write<uint32>(sl + rl + hl + 2 + 2 + 4);
	write<uint8>(0x0a);
	write<uint8>(sl);
	writeArray(sender, sl);
	write<uint8>(0x12);
	write<uint8>(rl);
	writeArray(receiver, rl);
	if(hl > 0)
	{
		writeArray(headerData, hl);
	}
	
	if(minOpcode)
		write<uint16>(opcode);
	else
		write<uint32>(opcode);
}
//*/
void PacketBuffer::writeHeader(uint8 sender[], uint8 sl, uint8 receiver[], uint8 rl, uint32 opcode, bool minOpcode)
{
	write<uint32>(0); // Write empty length
	write<uint32>(0); // write empty crc32
	if(minOpcode)
		write<uint32>(sl + rl + 2 + 2 + 2);
	else
		write<uint32>(sl + rl + 2 + 2 + 4);
	write<uint8>(0x0a);
	write<uint8>(sl);
	writeArray(sender, sl);
	write<uint8>(0x12);
	write<uint8>(rl);
	writeArray(receiver, rl);
	if(minOpcode)
		write<uint16>(opcode);
	else
		write<uint32>(opcode);
}
//*/
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
	/*
	printf("Send:\n");
	for(uint32 i = 0; i < bufferLength; i++)
	{
		printf("%02x ", buffer[i]);
	}
	printf("\n");
	//*/
	//printf("BuferLength: 0x%08x\n", bufferLength);
	send(client, (int8*)buffer, bufferLength, 0);
	drop();
}

void PacketBuffer::doItAllAgentServer(SOCKET client)
{
	finalizeAgentServer();
	send(client, (int8*)buffer, bufferLength, 0);
	drop();
}