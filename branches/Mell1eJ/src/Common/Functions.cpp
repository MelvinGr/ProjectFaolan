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

#include "Functions.h"

namespace String
{
	string GetIPFromSocket(SOCKET socket)
	{
		sockaddr_in m_addr;
		socklen_t len = sizeof(m_addr);
		getpeername(socket, (sockaddr*)&m_addr, &len);

		return inet_ntoa(m_addr.sin_addr);
	}

	string arrayToHexString(uint8* data, uint32 size)
	{
		if(size == 0)
			return "No Data! (Length = 0)";

		int8 buffer[256];
		string retstr;

		for (uint32 i = 0; i < size; i += 16)
		{
			sprintf(buffer, "%08X: ", i);
			retstr += buffer;

			for (int8 j = 0; j < 16; j++) 
			{
				if (i + j < size)
				{
					sprintf(buffer, "%02X ", data[i + j]);
					retstr += buffer;
				}
				else retstr += "   ";
			}

			retstr += " ";

			for (int8 j = 0; j < 16; j++) 
			{
				if (i + j < size)
				{
					sprintf(buffer, "%c", isprint(data[i + j]) ? data[i + j] : '.');
					retstr += buffer;
				}
			}

			retstr += "\n";
		}

		return retstr;
	}

	string serverInfoStr()
	{
		int8 tmp[1024];
		//sprintf(tmp, "Revision: %i\nBuild Date: %s\nBuild Time: %s\nPlatform: %s, %s %s\nCompiler: %s, version: %i", 
			//SVN_REVISION, __DATE__, __TIME__, PLATFORMNAME, CONFIG, ARCH, COMPILER, COMPILERVERSION);	

		return string(tmp);
	}

	vector<string> splitString(string str, string delim)
	{
		vector<string> results;

		uint32 cutAt;
		while((cutAt = str.find_first_of(delim)) != str.npos)
		{
			if(cutAt > 0)
				results.push_back(str.substr(0, cutAt));

			str = str.substr(cutAt + 1);
		}

		if(str.length() > 0) results.push_back(str);

		return results;
	}

	string replace(string str, string from, string to)
	{
		string newStr = str;
		int32 oldPosition = -2, position;
		while((position = newStr.find(from, oldPosition + 2)) != (int32)string::npos)
		{
			oldPosition = position;
			newStr = newStr.replace(position, from.length(), to);
		}

		return newStr;
	}

	string timeString()
	{
		time_t rawTime;
		time(&rawTime);
		tm* timeinfo = localtime(&rawTime);

		int8 tmp[128];
		sprintf(tmp, "%02i:%02i:%02i", timeinfo->tm_hour, timeinfo->tm_min, timeinfo->tm_sec);

		return string(tmp);
	}
}

bool FileExists(string strFilename)
{
#if PLATFORM == PLATFORM_WIN32
	return (GetFileAttributes(strFilename.c_str()) != INVALID_FILE_ATTRIBUTES);
#else
	struct stat stFileInfo;
	return(stat(strFilename.c_str(), &stFileInfo) == 0);
#endif
}