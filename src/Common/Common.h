/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published byd
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef COMMON_H
#define COMMON_H

#if defined(_WIN32) || defined(_WIN64)
	#define WINDOWS
	#define PLATFORMNAME "Windows"
#elif __CYGWIN__
	#define CYGWIN
	#define PLATFORMNAME "Windows (Cygwin)"
#elif __linux__
	#define LINUX
	#define PLATFORMNAME "Linux"
#elif __APPLE__
	#define MACOSX
	#define PLATFORMNAME "Mac OSX"
#else
	#define UNKNOWNOS
	#define PLATFORMNAME "UNKNOWN"
#endif

#ifdef _MSC_VER
	#define COMPILER "MS Visual Studio"
	#define COMPILERVERSION _MSC_VER
#elif __GNUC__
	#define COMPILER "GNU C++"
	#define COMPILERVERSION atoi(__VERSION__)
#else
	#define COMPILER "Unknown"
	#define COMPILERVERSION 0
#endif

#if defined(_DEBUG) || defined(DEBUG)
	#define CONFIG "Debug"
#else
	#define CONFIG "Release"
#endif

#if defined(X64) || defined(__amd64__) || defined(_WIN64)
    #define ARCH "X64"
#else
    #define ARCH "X86"
#endif

//////////////////////////////////////////////////////////////////////////

#if defined(WINDOWS) && defined(_MSC_VER)
	#pragma warning(disable: 4996) // This function or variable may be unsafe
	#pragma warning(disable: 4244) // conversion from 'my_ulonglong' to 'uint32', possible loss of data
	#pragma warning(disable: 4098) //
	#pragma warning(disable: 4482) // enum warning

	#pragma comment(lib, "ws2_32.lib")
	#pragma comment(lib, "libmysql.lib")
	#pragma comment(lib, "zlib.lib")
#endif

//////////////////////////////////////////////////////////////////////////

typedef char int8;
typedef short int16;
typedef int int32;

typedef unsigned char uint8;
typedef unsigned short uint16;
typedef unsigned int uint32;

typedef unsigned long ulong;
typedef long double ldouble;

#ifdef WINDOWS
	typedef __int64 int64;
	typedef unsigned __int64 uint64;
	typedef int32 socklen_t;

	#define atoi64(value) _atoi64(value)
	#define atoi64u(value) _atoi64(value)
#else
	typedef long long int int64;
	typedef unsigned long long int uint64;
	typedef int32 SOCKET;

	#define atoi64(value) strtoll(value, 0, 10)
	#define atoi64u(value) strtoull(value, 0, 10)
#endif

//////////////////////////////////////////////////////////////////////////

#define gameUnknown1 0x00000244
#define maxDistance 8000000

#define _USE_MATH_DEFINES

#include "SvnVariables.h"

#define CheckPacketCRC(packet)\
	(CRC32::CalculateCRC32((int8*)packet->packetBuffer->buffer, packet->packetBuffer->bufferLength) == packet->crc32)

//////////////////////////////////////////////////////////////////////////

#ifdef WINDOWS
	#define WIN32_LEAN_AND_MEAN 
#else
	#define closesocket(socket) shutdown(socket, SHUT_RDWR)
#endif

#endif
