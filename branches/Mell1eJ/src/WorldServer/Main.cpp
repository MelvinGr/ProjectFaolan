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

#include "../Common/Common.h"

#include <iostream>

#if PLATFORM != PLATFORM_WIN32
#include <pthread.h>
#include <signal.h>
#endif

#include <boost/bind.hpp>
#include <boost/function.hpp>
#include <boost/thread.hpp>

#include "../Common/Network.h"
#include "../Common/Functions.h"
#include "../Common/Configuration.h"
#include "../Common/MysqlDatabase.h"
#include "../Common/MysqlQuery.h"

#if 1 // DATABASE_TYPE == DATABASE_MYSQL
#include "../Common/MysqlFunctions.h"
#endif

#include "WorldConnection.h"
#include "InternalConnection.h"

using namespace std;

#if PLATFORM == PLATFORM_WIN32
boost::function0<void> console_ctrl_function;
BOOL WINAPI console_ctrl_handler(DWORD ctrl_type)
{
	switch (ctrl_type)
	{
	case CTRL_C_EVENT:
	case CTRL_BREAK_EVENT:
	case CTRL_CLOSE_EVENT:
	case CTRL_SHUTDOWN_EVENT:
		{
			console_ctrl_function();
			return TRUE;
		}

	default:
		return FALSE;
	}
}
#endif

int32 main(int32 argc, int8* argv[])
{
	try
	{
		printf("%s", FaolanBanner);

		srand((uint32)time(0));

		Config.parseConfigFile("FaolanConfig.cfg");
		Config.parseCommandLine(argc, argv);

		MysqlDatabase::createInstance(Config.GetValue<size_t>("demuxercount"), Config.GetValue<string>("dbusername"), 
			Config.GetValue<string>("dbhost"), Config.GetValue<string>("dbpassword"), Config.GetValue<string>("dbname"), 
			Config.GetValue<uint32>("dbport"));

		if(!MysqlDB->start())
			throw runtime_error("Could not connect to the Database!");

		boost::asio::io_service ioService;
		InternalConnection* ic = new InternalConnection(ioService, 0,
			Config.GetValue<string>("faolanmanageripaddress"), Config.GetValue<uint32>("faolanmanagerport"), WorldServer);

		boost::thread icThread(boost::bind(&InternalConnection::start, ic));

		if (ic->waitUntilConnected())
			printf("Connected to the Manager!\n");
		else
			throw runtime_error("Could not connect to the Manager!");

		cin.get();

		Network n;
		//n.createConnectionAcceptor<WorldConnection>(realmInfo.worldServerIPAddress, 
		//	realmInfo.worldServerPort, Config.GetValue<size_t>("demuxerCount"));	

#if PLATFORM == PLATFORM_WIN32
		console_ctrl_function = boost::bind(&Network::stop, &n);
		SetConsoleCtrlHandler(console_ctrl_handler, TRUE);
#else
		sigset_t wait_mask;
		sigemptyset(&wait_mask);
		sigaddset(&wait_mask, SIGINT);
		sigaddset(&wait_mask, SIGQUIT);
		sigaddset(&wait_mask, SIGTERM);
		pthread_sigmask(SIG_BLOCK, &wait_mask, 0);

		int32 sig = 0;
		sigwait(&wait_mask, &sig);
#endif

		n.wait();
	}
	catch (exception& e)
	{
		printf("Unhandled Exception: %s\n",  e.what());
	}

	MysqlDatabase::destroy();
	Configuration::destroy();

	return 0;
}