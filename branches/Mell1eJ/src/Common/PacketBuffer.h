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

#ifndef PACKETBUFFER_H
#define PACKETBUFFER_H

#include "Common.h"

#if PLATFORM == PLATFORM_WIN32
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
#include "Functions.h"

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

	~PacketBuffer()
	{
		delete[] buffer;

		offset = 0;
		bufferLength = 0;
		maxLength = 0;
	}

	template <typename T> T read()
	{
		try
		{
			T ret = *(reinterpret_cast<T*>(&buffer[offset]));
			SwapByte::Swap<T>(ret);

			offset += sizeof(T);
			return ret;
		}
		catch(const std::string &msg)
		{
			printf("Error while reading %s: %s\n", typeid(T).name(), msg.c_str());
			return NULL;
		}
	}

	template <typename T> void write(const T &data)
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
		catch(const std::string &msg)
		{
			printf("Error while writing %s: %s\n", typeid(T).name(), msg.c_str());
		}
	}

	void reset();
	void resize(uint32 size);

	uint8* read(uint32 length);
	void write(const uint8* data, uint32 length);
	void finalize();

	inline std::string toString() { return String::arrayToHexString(buffer, bufferLength); }
};

template <> inline std::string PacketBuffer::read()
{
	try
	{
		assert((offset + sizeof(int16)) <= bufferLength);
		uint16 stringLength = read<uint16>();

		assert((offset + stringLength) <= bufferLength);
		std::string data(&buffer[offset], &buffer[offset + stringLength]);

		offset += stringLength;
		return data;
	}
	catch(const std::string &msg)
	{
		printf("Error while reading string: %s\n", msg.c_str());
		return "";
	}
}

template <> inline void PacketBuffer::write(const std::string &data) 
{
	try
	{
		assert((offset + sizeof(int16)) <= maxLength);
		write<uint16>(data.size());

		assert((offset + data.size()) <= maxLength);
		write((uint8*)data.c_str(), data.size());
	}
	catch(const std::string &msg)
	{
		printf("Error while writing string: %s\n", msg.c_str());
	}
}

#endif