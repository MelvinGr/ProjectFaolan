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

#include "Networking.h"

Networking::Networking(int32 nPort)
{
	errorCode = 0;
	port = nPort;

}

int32 Networking::initDB()
{
	if(!Database.initMysql(Settings.mysqlAddress, Settings.mysqlPort, Settings.mysqlAccount, Settings.mysqlPassword, Settings.mysqlDatabase))
	{
		Log.Error("Could not connect to Mysql Host!\n");
		return -5;
	}
	return 0;
}

int32 Networking::start(string waitTxt)
{
#ifdef WINDOWS
	WSADATA wsdata;
	if(WSAStartup(MAKEWORD(2, 2), &wsdata) != 0)
	{
		Log.Error("WSAStartup() has failed. WSAGetLastError: %i\n", WSAGetLastError());
		WSACleanup();
		return -1;
	}

	//Log.Success("WSAStartup() Successful!\n");
#endif

	if((sock = socket(AF_INET, SOCK_STREAM, 0)) < 0)
	{
#ifdef WINDOWS
		Log.Error("socket() failed WSAGetLastError: %i\n", WSAGetLastError());
		WSACleanup();
#else
		Log.Error("socket() failed! errno: %i\n", errno);
#endif
		return -2;
	}

	//Log.Success("Socket() Successful!\n");

	sin.sin_port = htons(port);
	sin.sin_addr.s_addr = INADDR_ANY;
	sin.sin_family = AF_INET;

	sockAddrLen = sizeof(sin);

	if(bind(sock,(sockaddr*)&sin, sockAddrLen) < 0)
	{
#ifdef WINDOWS
		Log.Error("bind() failed WSAGetLastError: %i\n", WSAGetLastError());
		WSACleanup();
#else
		Log.Error("bind() failed! errno: %i\n", errno);
#endif
		return -3;
	}

	//Log.Success("Bind() Successful!\n");

	if(listen(sock, SOMAXCONN) < 0)
	{
#ifdef WINDOWS
		Log.Error("listen() failed WSAGetLastError: %i\n", WSAGetLastError());
		WSACleanup();
#else
		Log.Error("listen() failed! errno: %i\n", errno);
#endif
		return -4;
	}

	//Log.Success("Listen() Successful!\n");
	/*
	if(!Database.initMysql(Settings.mysqlAddress, Settings.mysqlPort, Settings.mysqlAccount, Settings.mysqlPassword, Settings.mysqlDatabase))
	{
		Log.Error("Could not connect to Mysql Host!\n");
		return -5;
	}
	//*/
	//Log.Success("Connected to MySQL Server!\n");
	if(waitTxt.size() > 0)
		Log.Success(waitTxt.c_str());
	else
		Log.Success("- Waiting for Connections!\n");

	return 0;
}

SOCKET Networking::AcceptClient()
{
	return accept(sock, (sockaddr*)&sin, &sockAddrLen);
}

void Networking::close()
{
	Database.closeMysql();
	closesocket(sock);

#ifdef WINDOWS
	WSACleanup();
#endif
}
