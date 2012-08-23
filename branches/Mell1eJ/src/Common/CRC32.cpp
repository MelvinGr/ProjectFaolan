/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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

#include "CRC32.h"

namespace CRC32
{
	uint32 CalculateCRC32(const uint8* value, size_t length)
	{
		uint32* buffer = (uint32*)value + 2;

		size_t len = length - 8;
		uint32 crc32 = 0xffffffff;
		size_t offset = 0;

		if (len >= 32)
		{
			for(size_t i = 0; i < len >> 5; i++)
			{
				crc32 ^= buffer[offset];
				len -= 4;

				for(uint8 i = 0; i < 7; i++)
				{
					offset++;
					crc32 = crcTab2[crc32 >> 16 & 0xFF] ^ crcTab3[crc32 >> 8 & 0xFF] ^ crcTab1[crc32 >> 24] ^ crcTab4[crc32 & 0xFF] ^ buffer[offset];
					len -= 4;
				}

				crc32 = crcTab2[crc32 >> 16 & 0xFF] ^ crcTab3[crc32 >> 8 & 0xFF] ^ crcTab1[crc32 >> 24] ^ crcTab4[crc32 & 0xFF];
				offset++;
			}
		}

		if (len >= offset)
		{
			for (size_t i = 0; i < (len >> 2); i++)
			{
				crc32 ^= buffer[offset];
				len -= 4;

				crc32 = crcTab2[crc32 >> 16 & 0xFF] ^ crcTab3[crc32 >> 8 & 0xFF] ^ crcTab1[crc32 >> 24] ^ crcTab4[crc32 & 0xFF];
				offset++;
			}
		}

		if (len > 0)
		{
			offset *= 4;

			for (size_t i = 0; i < len; i++)
			{
#if BITPLATFORM == PLATFORM_32BIT
				uint8 tmp = *(uint8*)((uint32)buffer + offset);
#else
                uint8 tmp = *(uint8*)((uint64)buffer + offset);
#endif
				crc32 = (crc32 >> 8) ^ crcTab1[(tmp ^ crc32) & 0xFF];
				offset++;
			}
		}

		return crc32 ^ 0xFFFFFFFF;
	}
};
