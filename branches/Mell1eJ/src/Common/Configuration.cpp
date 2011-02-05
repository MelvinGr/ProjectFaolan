/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

#include "Configuration.h"

#define bvalue boost::program_options::value
Configuration::Configuration() : Singleton<Configuration>(), m_description("server options"), m_addressCounter(0)
{
	m_description.add_options()
		("DBUsername", 				bvalue<string>(&DBUsername)->default_value("root"), 					"DB Username")
		("DBPassword", 				bvalue<string>(&DBPassword)->default_value("toor"), 					"DB password")
		("DBHost", 					bvalue<string>(&DBHost)->default_value("127.0.0.1"), 					"DB Host")
		("DBPort", 					bvalue<uint32>(&DBPort)->default_value(3306), 							"DB port")
		("DBName", 					bvalue<string>(&DBName)->default_value("faolan"), 						"DB name")
		("DBConnectionCount",		bvalue<uint32>(&DBConnectionCount)->default_value(5), 					"Number of active connection to the DB server")

		("universeAgentAddress", 	bvalue<string>(&universeAgentAddress)->default_value("127.0.0.1"),		"Address to listen to")
		("universeAgentPort", 		bvalue<uint32>(&universeAgentPort)->default_value(7000), 				"Port to listen to")

		("playerAgentAddress", 		bvalue<string>(&playerAgentAddress)->default_value("127.0.0.1"),		"Address to listen to")
		("playerAgentPort", 		bvalue<uint32>(&playerAgentPort)->default_value(7001), 					"Port to listen to")

		("csPlayerAgentAddress", 	bvalue<string>(&csPlayerAgentAddress)->default_value("127.0.0.1"),		"Address to listen to")
		("csPlayerAgentPort", 		bvalue<uint32>(&csPlayerAgentPort)->default_value(7002), 				"Port to listen to")

		("agentServerAddress", 		bvalue<string>(&agentServerAddress)->default_value("127.0.0.1"),		"Address to listen to")
		("agentServerPort", 		bvalue<uint32>(&agentServerPort)->default_value(7003), 					"Port to listen to")

		("worldServerAddress", 		bvalue<string>(&worldServerAddress)->default_value("127.0.0.1"),		"Address to listen to")
		("worldServerPort", 		bvalue<uint32>(&worldServerPort)->default_value(7004), 					"Port to listen to")

		("demuxerCount", 			bvalue<uint32>(&demuxerCount)->default_value(2), 						"Count of network demuxer threads")

		("realmName", 				bvalue<string>(&realmName)->default_value("Project Faolan Realm"),		"Realm Name")
		("characterSlots", 			bvalue<uint32>(&characterSlots)->default_value(4), 						"Number of character slots");
}

void Configuration::parseCommandLine(int argc, char *argv[])
{
	boost::program_options::store(boost::program_options::parse_command_line(argc, argv, m_description), m_variableMap);
	boost::program_options::notify(m_variableMap);

	if (argc < 2 || (!strcmp(argv[1], "-?") || !strcmp(argv[1], "--?") || !strcmp(argv[1], "/?") || !strcmp(argv[1], "/h") || !strcmp(argv[1], "-h") || !strcmp(argv[1], "--h") || !strcmp(argv[1], "--help") || !strcmp(argv[1], "/help") || !strcmp(argv[1], "-help") || !strcmp(argv[1], "help")))
	{
		printConfiguration();
	}
}

void Configuration::parseConfigFile()
{
	ifstream ifs("FaolanConfig.cfg");
	if(ifs.is_open())
	{
		boost::program_options::store(boost::program_options::parse_config_file(ifs, m_description), m_variableMap);
		boost::program_options::notify(m_variableMap);
		ifs.close();
	}
	else
	{
		cout << "Can't open the configuration file" << endl;
	}
}

void Configuration::printConfiguration()
{
	//cout << m_description << endl;
}