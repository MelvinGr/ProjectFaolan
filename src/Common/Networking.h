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

#ifndef NETWORKING_H
#define NETWORKING_H

#include "Common.h"

#ifdef WINDOWS
#include <Winsock2.h>
#else
#include <sys/types.h>
#include <sys/socket.h>
#include <errno.h>
#endif

#include <stdio.h>
#include <vector>

#include "Settings.h"
#include "MysqlDatabase.h"

class Networking
{
	SOCKET sock;
	int32 port;

	sockaddr_in sin;
	socklen_t sockAddrLen;

public:
	int32 errorCode;

	Networking(int32 nPort);
	int32 initDB();
	int32 start();
	SOCKET AcceptClient();
	void close();
};

#endif
