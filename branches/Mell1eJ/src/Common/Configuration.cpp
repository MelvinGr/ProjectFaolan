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

#include "Configuration.h"

#define bvalue boost::program_options::value
Configuration::Configuration() : Singleton<Configuration>(), m_description("server options")
{
	m_description.add_options()
		("DBUsername",				boost::program_options::value<string>(&DBUsername),				"DB Username")
		("DBPassword",				boost::program_options::value<string>(&DBPassword),				"DB password")
		("DBHost",					boost::program_options::value<string>(&DBHost),					"DB Host")
		("DBPort",					boost::program_options::value<uint32>(&DBPort),					"DB port")
		("DBName",					boost::program_options::value<string>(&DBName),					"DB name")
		("DBConnectionCount",		boost::program_options::value<uint32>(&DBConnectionCount),		"Number of active connection to the DB server")
		("DBType",					boost::program_options::value<uint32>(&DBType),					"DB Type")

		("demuxerCount",			boost::program_options::value<uint32>(&demuxerCount),			"Count of network demuxer threads")
		("characterSlots",			boost::program_options::value<uint32>(&characterSlots),			"Number of character slots")

		("universeAgentAddress",	boost::program_options::value<string>(&universeAgentAddress),	"Address to listen to")
		("universeAgentPort",		boost::program_options::value<uint32>(&universeAgentPort),		"Port to listen to")

		("playerAgentAddress",		boost::program_options::value<string>(&playerAgentAddress),		"Address to listen to")
		("playerAgentPort",			boost::program_options::value<uint32>(&playerAgentPort),		"Port to listen to");
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
		throw runtime_error("Can't open the configuration file");
	}
}

void Configuration::printConfiguration()
{
	//cout << m_description << endl;
}