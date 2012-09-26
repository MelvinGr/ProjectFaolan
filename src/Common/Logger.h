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

#ifndef LOGGER_H
#define LOGGER_H

#include "Common.h"
#include "Singleton.h"
#include "Mutex.h"
#include "Functions.h"

#include <stdio.h>
#include <fstream>
#include <stdarg.h>

using namespace std;

#ifdef WIN32
	#define TRED FOREGROUND_RED | FOREGROUND_INTENSITY
	#define TGREEN FOREGROUND_GREEN | FOREGROUND_INTENSITY
	#define TYELLOW FOREGROUND_GREEN | FOREGROUND_RED | FOREGROUND_INTENSITY
	#define TNORMAL FOREGROUND_GREEN | FOREGROUND_RED | FOREGROUND_BLUE
	#define TWHITE TNORMAL | FOREGROUND_INTENSITY
	#define TBLUE FOREGROUND_BLUE | FOREGROUND_GREEN | FOREGROUND_INTENSITY
#else
	#define TRED 1
	#define TGREEN 2
	#define TYELLOW 3
	#define TNORMAL 4
	#define TWHITE 5
	#define TBLUE 6
#endif

#define Log Logger::getSingleton()
class Logger : public Singleton<Logger>
{
	Mutex mutex;
	FILE* file;

#ifdef WIN32
	HANDLE stdoutHandle, stderrHandle;
#endif  

public:
	string logFile;
	int8 logLevel, fileLogLevel;

	Logger();
	~Logger();

	bool Start(string fileName);

	void Color(uint32 color);
	void Line();

	void Notice(const int8* format, ...);
	void Warning(const int8* format, ...);
	void Error(const int8* format, ...);
	void Success(const int8* format, ...);
	void Debug(const int8* format, ...);
};

#endif
