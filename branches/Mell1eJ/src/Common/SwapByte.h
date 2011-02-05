/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

#ifndef SWAPBYTE_H
#define SWAPBYTE_H

#include "Common.h"

namespace SwapByte
{
	inline bool BigEndian()
	{
		int16 w = 0x4321;
		return (*(uint8*)&w != 0x21);
	}

	template <typename T> inline void Swap(T& value) {}

	template <> inline void Swap(uint16& value)
	{
		if(!BigEndian()) 
			value = ((((uint16)(value) & (uint16)0x00ffU) << 8) | (((uint16)(value) & (uint16)0xff00U) >> 8));
	}

	template <> inline void Swap(int16& value)
	{
		if(!BigEndian()) 
			value = ((((int16)(value) & (int16)0x00ffU) << 8) | (((int16)(value) & (int16)0xff00U) >> 8));
	}

	template <> inline void Swap(int32& value)
	{
		if(!BigEndian()) 
			value = ((((int32)(value) & (int32)0x000000ffU) << 24) | (((int32)(value) & (int32)0x0000ff00U) <<  8) |
			(((int32)(value) & (int32)0x00ff0000U) >>  8) | (((int32)(value) & (int32)0xff000000U) >> 24));
	}

	template <> inline void Swap(uint32& value)
	{
		if(!BigEndian()) 
			value = ((((uint32)(value) & (uint32)0x000000ffU) << 24) | (((uint32)(value) & (uint32)0x0000ff00U) <<  8) |
			(((uint32)(value) & (uint32)0x00ff0000U) >>  8) | (((uint32)(value) & (uint32)0xff000000U) >> 24));
	}

	template <> inline void Swap(int64& value)
	{
		if(!BigEndian()) 
			value = ((((int64)(value) & (int64)0x00000000000000ffULL) << 56) | (((int64)(value) & (int64)0x000000000000ff00ULL) << 40) |
			(((int64)(value) & (int64)0x0000000000ff0000ULL) << 24) | (((int64)(value) & (int64)0x00000000ff000000ULL) <<  8) |
			(((int64)(value) & (int64)0x000000ff00000000ULL) >>  8) | (((int64)(value) & (int64)0x0000ff0000000000ULL) >> 24) | 
			(((int64)(value) & (int64)0x00ff000000000000ULL) >> 40) | (((int64)(value) & (int64)0xff00000000000000ULL) >> 56));
	}

	template <> inline void Swap(uint64& value)
	{
		if(!BigEndian())
			value = ((((uint64)(value) & (uint64)0x00000000000000ffULL) << 56) |  (((uint64)(value) & (uint64)0x000000000000ff00ULL) << 40) |
			(((uint64)(value) & (uint64)0x0000000000ff0000ULL) << 24) | (((uint64)(value) & (uint64)0x00000000ff000000ULL) <<  8) |
			(((uint64)(value) & (uint64)0x000000ff00000000ULL) >>  8) | (((uint64)(value) & (uint64)0x0000ff0000000000ULL) >> 24) |
			(((uint64)(value) & (uint64)0x00ff000000000000ULL) >> 40) | (((uint64)(value) & (uint64)0xff00000000000000ULL) >> 56));
	}
};
#endif