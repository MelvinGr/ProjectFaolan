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

#include "Logger.h"

CreateFileSingleton(Logger);

Logger::Logger()
{
	logLevel = 3;
	fileLogLevel = 3;

#ifdef WIN32
	stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
	stderrHandle = GetStdHandle(STD_ERROR_HANDLE);
#endif
}

Logger::~Logger()
{
	fclose(file);
}

bool Logger::Start(string fileName)
{
	if(fileName == "" || fileLogLevel == 0)
		return false;

	logFile = fileName;

	file = fopen(logFile.c_str(), "w");

	if(file)
	{
		fprintf(file, "\n\n-- New Log: %s --\n\n", String::timeString().c_str());
		return true;
	}
	else return false;
}

void Logger::Color(uint32 color)
{
#ifndef WIN32
	static const char* colorstrings[TBLUE + 1] = 
	{
		"",
		"\033[22;31m",
		"\033[22;32m",
		"\033[01;33m",
		//"\033[22;37m",
		"\033[0m",
		"\033[01;37m",
		"\033[1;34m",
	};

	fputs(colorstrings[color], stdout);
#else
	SetConsoleTextAttribute(stdoutHandle, (WORD)color);
#endif
}

void Logger::Line()
{
	mutex.Acquire();

	printf("\n");

	if(file)
	{
		fprintf(file, "\n");
		fflush(file);
	}

	mutex.Release();
}

void Logger::Notice(const int8* format, ...)
{
	if(logLevel < 1)
		return;

	mutex.Acquire();

	va_list ap;
	va_start(ap, format);

	vprintf(format, ap);

	if(file && fileLogLevel >= 1)
	{
		vfprintf(file, format, ap);
		fflush(file);
	}

	va_end(ap);

	mutex.Release();
}

void Logger::Warning(const int8* format, ...)
{
	if(logLevel < 2)
		return;

	mutex.Acquire();

	va_list ap;
	va_start(ap, format);

	Color(TYELLOW);
	vprintf(format, ap);
	Color(TNORMAL);

	if(file && fileLogLevel >= 2)
	{
		vfprintf(file, format, ap);
		fflush(file);
	}

	va_end(ap);
	mutex.Release();
}

void Logger::Error(const int8* format, ...)
{
	if(logLevel < 1)
		return;

	mutex.Acquire();

	va_list ap;
	va_start(ap, format);	

	Color(TRED);
	vprintf(format, ap);
	Color(TNORMAL);

	if(file && fileLogLevel >= 1)
	{
		vfprintf(file, format, ap);
		fflush(file);
	}

	va_end(ap);
	mutex.Release();
}

void Logger::Success(const int8* format, ...)
{
	if(logLevel < 2)
		return;

	mutex.Acquire();

	va_list ap;
	va_start(ap, format);

	Color(TGREEN);
	vprintf(format, ap);
	Color(TNORMAL);

	if(file && fileLogLevel >= 2)
	{
		vfprintf(file, format, ap);
		fflush(file);
	}

	va_end(ap);
	mutex.Release();
}

void Logger::Debug(const int8* format, ...)
{
	if(logLevel < 3)
		return;

	mutex.Acquire();

	va_list ap;
	va_start(ap, format);

	Color(TBLUE);
	vprintf(format, ap);
	Color(TNORMAL);

	if(file && fileLogLevel >= 3)
	{
		vfprintf(file, format, ap);
		fflush(file);
	}

	va_end(ap);
	mutex.Release();
}