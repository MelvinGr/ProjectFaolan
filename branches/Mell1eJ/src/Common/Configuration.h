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

#ifndef CONFIGURATION_H
#define CONFIGURATION_H

#include "Common.h"

#include <iostream>
#include <fstream>

#include <boost/program_options.hpp>
#include <boost/thread.hpp>

#include "Singleton.h"

using namespace std;

class Database;

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

public:
	void parseCommandLine(int argc, int8* argv[]);
	void parseConfigFile();
	void printConfiguration();

	string DBUsername, DBPassword, DBName, DBHost;
	uint32 DBPort, DBConnectionCount, DBType;

	uint32 demuxerCount;
	uint32 characterSlots;
	
	string universeAgentAddress;
	uint32 universeAgentPort;

	string playerAgentAddress;
	uint32 playerAgentPort;
};

#endif
