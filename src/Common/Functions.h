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

#ifndef FUNCTIONS_H
#define FUNCTIONS_H

#include "Common.h"

#ifdef WINDOWS
#include <Winsock2.h>
#else
#include <sys/socket.h>
#include <arpa/inet.h>
#include <limits.h>
#include <errno.h>
#include <linux/version.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

#include <iostream>
#include <sstream>
#include <iomanip>
#include <fstream>
#include <vector>
#include <algorithm>
#include <stdio.h>
#include <time.h>
#include <zlib.h>

#include "SwapByte.h"
#include "PacketBuffer.h"
#include "Packet.h"
#include "Vector3D.h"
#include "Logger.h"

using namespace std;

namespace Network
{
	string GetIPFromSocket(SOCKET socket);
	int32 sendToAgentServer(string agentServerIP, uint32 agentServerPort, uint32 opcode, uint32 nClientInst, string text);
}

namespace String
{
	template <typename T> string toString(T data)
	{
		std::stringstream ss;
		ss << data;
		return ss.str();
	}

	string arrayToHexString(uint8* data, uint32 size);
	string faolanBanner();
	string serverInfoStr();
	vector<string> splitString(string str, string delim);
	string replace(string str, string from, string to);
	string timeString();
}     

bool FileExists(string strFilename);

#endif
