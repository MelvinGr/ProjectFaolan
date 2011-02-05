/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

#include "CSPlayerAgent.h"

vector<GameClient*>* clientList = new vector<GameClient*>();

void CSPlayerAgent::HandleClient(void* socket)
{
	GameClient* client = new GameClient(*((uint32*)socket));
	clientList->push_back(client);

	while(client->isConnected)
	{
		Packet* packet = client->getNextPacket(false);
		if(packet)
		{
			CSPlayerAgentHandler(packet, client);
			delete packet;
		}
	}

	remove(clientList->begin(), clientList->end(), client); 
	delete client;
}

int32 main(int32 argc, int8* argv[], int8* envp[])
{
#ifdef WINDOWS
	SetConsoleTitle("Project Faolan - CSPlayerAgent");
#endif

	Log.Start("CSPlayerAgent.txt");

	if(!Settings.load("./FaolanConfig.cnf", envp)) 
		return -9;

	Log.Notice("%s\n%s\n", String::faolanBanner().c_str(), String::serverInfoStr().c_str());

	Networking network(Settings.csPlayerAgentPort);
	int32 result = network.initDB();
	if(result == 0)
		int32 result = network.start();
	if(result == 0) 
	{
		while(true)
		{
			SOCKET client = network.AcceptClient();

#ifdef WINDOWS
			CreateThread(0, 0, (LPTHREAD_START_ROUTINE)CSPlayerAgent::HandleClient, &client, 0, 0);
#else
			pthread_create(0, 0, (void*(*)(void*))CSPlayerAgent::HandleClient, &client);
#endif
		}
	}
	else Log.Error("Error while setting up the Networking, errorcode: %u!\n", result);

	return result;
}