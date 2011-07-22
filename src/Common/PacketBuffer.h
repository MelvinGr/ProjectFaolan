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

#ifndef PACKETBUFFER_H
#define PACKETBUFFER_H

#include "Common.h"

#ifdef WINDOWS
#include <Winsock2.h>
#else
#include <sys/socket.h>
#endif

#include <iostream>
#include <string>
#include <assert.h>
#include <string.h>
#include <stdio.h>

#include "CRC32.h"
#include "SwapByte.h"
//#include "Client.h"

using namespace std;

struct PacketBuffer
{
public:
	uint32 offset;
	uint8* buffer;
	uint32 bufferLength;
	uint32 maxLength;

	PacketBuffer() {}

	PacketBuffer(uint32 length)
	{
		offset = 0;
		bufferLength = 0;
		maxLength = length;
		buffer = new uint8[length];
	}

	PacketBuffer(uint8* data, uint32 length)
	{
		offset = 0;
		bufferLength = length;
		maxLength = length;
		buffer = new uint8[length];

		memcpy(buffer, data, length);
	}

	void drop()
	{
		delete[] buffer;

		offset = 0;
		bufferLength = 0;
		maxLength = 0;
	}

	template <typename T> T read(bool set2B = true)
	{
		try
		{
			T ret = *(reinterpret_cast<T*>(&buffer[offset]));
			SwapByte::Swap<T>(ret);

			offset += sizeof(T);
			return ret;
		}
		catch(char* msg)
		{
			printf("Error at reading @ PacketBuffer.h\nErrorMsg: %s \n", msg);
			return NULL;
		}
	}

	template <typename T> void write(T data, bool set2B = true)
	{
		try
		{
			T newData = data;
			SwapByte::Swap<T>(newData);

			assert((offset + sizeof(T)) <= maxLength);
			memcpy(&buffer[offset], reinterpret_cast<uint8*>(&newData), sizeof(newData));

			offset += sizeof(newData);
			bufferLength += sizeof(newData);
		}
		catch(char* msg)
		{
			printf("Error at writing @ PacketBuffer.h\nErrorMsg: %s \n", msg);
		}
	}

	uint8* readArray(uint32 length);
	void writeArray(uint8* data, uint32 length);
	void writeHeader(string sender, string receiver, uint32 unknown1, uint32 unknown2, uint32 user, uint32 unknown4, uint32 opcode);
	void finalize();
	void finalizeAgentServer();
	void doItAll(SOCKET client);
	void doItAllAgentServer(SOCKET client);
};


template <> inline void PacketBuffer::write(string data, bool set2B)
{
	try
	{
		if(set2B)
		{
			assert((offset + sizeof(int16)) <= maxLength);
			write<uint16>(data.size());
		}
		else
		{
			assert((offset + sizeof(int8)) <= maxLength);
			write<uint8>(data.size());
		}
	
		assert((offset + data.size()) <= maxLength);
		writeArray((uint8*)data.c_str(), data.size());
	}
	catch(char* msg)
	{
		printf("Error at writing String @ PacketBuffer.h\nErrorMsg: %s \n", msg);
	}
}

template <> inline string PacketBuffer::read(bool set2B)
{
	try
	{
		uint16 stringLength = 0;
		if(set2B)
		{
			assert((offset + sizeof(int16)) <= bufferLength);
			stringLength = read<uint16>();
		}
		else
		{
			assert((offset + sizeof(int8)) <= bufferLength);
			stringLength = read<uint8>();
			stringLength &= 0x00000000ffffffff;
		}
	
		assert((offset + stringLength) <= bufferLength);
		string data(&buffer[offset], &buffer[offset + stringLength]);
	
		offset += stringLength;
		return data;
	}
	catch(char* msg)
	{
		printf("Error at reading String @ PacketBuffer.h\nErrorMsg: %s \n", msg);
		return "";
	}
}

#endif
