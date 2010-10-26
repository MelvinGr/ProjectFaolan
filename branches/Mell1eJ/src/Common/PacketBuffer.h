/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

#ifndef BUFFER_H
#define BUFFER_H

#include "Common.h"

#include <vector>
#include <memory>
#include <string>

#include <boost/assert.hpp>
#include <boost/asio.hpp>

#include "SwapByte.h"
#include "CRC32.h"

using namespace std;

class PacketBuffer 
{
	vector<uint8> m_rawBuffer;
	uint32 m_writeCursor, m_readCursor;
	size_t m_size;

public:
	PacketBuffer() : m_rawBuffer(0), m_writeCursor(0), m_readCursor(0), m_size(0) { }
	PacketBuffer(size_t chunkSize) : m_rawBuffer(chunkSize), m_writeCursor(0), m_readCursor(0), m_size(0) { }
	PacketBuffer(uint8* data, size_t chunkSize) : m_rawBuffer(chunkSize), m_writeCursor(0), m_readCursor(0), m_size(0) { write(data, chunkSize); }

	size_t size()
	{
		return m_size;
	}

	size_t capacity()
	{
		return m_rawBuffer.size();
	}

	void resize(size_t size)
	{
		m_rawBuffer.resize(size);
	}

	void reset()
	{
		m_size = 0;
		m_writeCursor = 0;
		m_readCursor = 0;
	}

	const uint8* constCharBuffer() const
	{
		return &m_rawBuffer[0];
	}

	const vector<uint8>& constBuffer() const
	{
		return m_rawBuffer;
	}

	vector<uint8>& mutBuffer()
	{
		return m_rawBuffer;
	}

	template <typename T> void write(T data)
	{
		SwapByte::Swap<T>(data);
		write(reinterpret_cast<uint8*>(&data), sizeof(data));
	}

	void write(const uint8* data, size_t length)
	{
		BOOST_ASSERT((m_writeCursor + length) <= m_rawBuffer.size());
		memcpy(&m_rawBuffer[m_writeCursor], data, length);

		m_writeCursor += length;
		m_size += length;
	}

	template <typename T> T read()
	{
		BOOST_ASSERT((m_readCursor + sizeof(T)) <= m_rawBuffer.size());

		T ret = *(reinterpret_cast<T*>(&m_rawBuffer[m_readCursor]));
		SwapByte::Swap<T>(ret);

		m_readCursor += sizeof(T);
		return ret;
	}

	unsigned char* read(unsigned int length)
	{
		BOOST_ASSERT((m_readCursor + length) <= m_rawBuffer.size());

		uint8* ret = new uint8[length];
		for(uint32 i = 0; i < length; i++) 
			ret[i] = read<uint8>();

		return ret;
	}
	
	void write(string data)
	{
		BOOST_ASSERT((m_writeCursor + data.length() + sizeof(uint16)) <= m_rawBuffer.size());
		
		write<uint16>(data.length());
		write((uint8*)data.c_str(), data.length());
	}
	
	string read()
	{
		BOOST_ASSERT((m_readCursor + sizeof(uint16)) <= m_rawBuffer.size());
		uint16 stringSize = read<uint16>();
		
		BOOST_ASSERT((m_readCursor + stringSize) <= m_rawBuffer.size());
		string ret(&m_rawBuffer[m_readCursor], &m_rawBuffer[m_readCursor + stringSize]);
		
		m_readCursor += stringSize;
		return ret;
	}

	void writeHeader(string sender, string receiver, uint32 unknown1, uint32 unknown2, uint32 user, uint32 unknown4, uint32 opcode)
	{
		reset();

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

	void finalize()
	{
		uint32 packetLength = m_size - sizeof(uint32); 
		SwapByte::Swap<uint32>(packetLength);
		memcpy(&m_rawBuffer[0], reinterpret_cast<uint8*>(&packetLength), sizeof(uint32));

		uint32 hash = CRC32::CalculateCRC32(&m_rawBuffer[0], m_size);
		SwapByte::Swap<uint32>(hash);
		memcpy(&m_rawBuffer[4], reinterpret_cast<uint8*>(&hash), sizeof(uint32));
	}
};

#endif