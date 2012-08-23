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

using namespace std;

void PacketBuffer::reset()
{
	memset(buffer, 0, maxLength);

	offset = 0;
	bufferLength = 0;
}

void PacketBuffer::resize(size_t size)
{
	buffer = (uint8*)realloc(buffer, size);
	maxLength = size;
}

uint8* PacketBuffer::read(size_t length)
{
	try
	{
		assert((offset + length) <= bufferLength);
		uint8* ret = new uint8[length];
		for(size_t i = 0; i < length; i++) 
			ret[i] = read<uint8>();

		return ret;
	}
	FAOLAN_CATCH
    
    return 0;
}

void PacketBuffer::write(const uint8* data, size_t length)
{
	try
	{
		assert((offset + length) <= maxLength);
		for(size_t i = 0; i < length; i++)
			write<uint8>(data[i]);
	}
	FAOLAN_CATCH
}

void PacketBuffer::finalize()
{
	uint32 packetLength = (uint32)bufferLength - sizeof(uint32);
	SwapByte::Swap<uint32>(packetLength);
	memcpy(&buffer[0], reinterpret_cast<uint8*>(&packetLength), sizeof(uint32));

	uint32 hash = CRC32::CalculateCRC32(buffer, bufferLength);
	SwapByte::Swap<uint32>(hash);
	memcpy(&buffer[4], reinterpret_cast<uint8*>(&hash), sizeof(uint32));
}