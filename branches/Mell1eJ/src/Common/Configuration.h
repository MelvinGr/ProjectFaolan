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

#ifndef Configuration_H
#define Configuration_H

#include "Common.h"

#include <iostream>
#include <fstream>

#include <boost/program_options.hpp>
#include <boost/thread.hpp>

#include "Singleton.h"

using namespace std;

/**
* server configuration manager
* @author Albator
*/
#define Config Configuration::instance()
class Configuration : public Singleton<Configuration>
{
	friend class Singleton<Configuration>;

	Configuration();

	boost::program_options::options_description m_description;
	boost::program_options::variables_map m_variableMap;

	boost::mutex m_mutex;
	uint32 m_addressCounter;
	vector<string> m_charServers;

public:
	void parseCommandLine(int argc, char* argv[]);
	void parseConfigFile();
	void printConfiguration();
	void generateFinalOptions();

	string DBUsername, DBPassword, DBName, DBHost;
	uint32 DBPort, DBConnectionCount;
	
	string universeAgentAddress;
	uint32 universeAgentPort;

	string playerAgentAddress;
	uint32 playerAgentPort;

	string csPlayerAgentAddress;
	uint32 csPlayerAgentPort;

	string agentServerAddress;
	uint32 agentServerPort;

	string worldServerAddress;
	uint32 worldServerPort;

	uint32 demuxerCount;

	string realmName;
	uint32 characterSlots;
};

#endif
