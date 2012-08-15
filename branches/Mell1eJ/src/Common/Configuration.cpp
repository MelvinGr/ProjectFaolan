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

#include <iostream>
#include <fstream>

using namespace std;

Configuration::Configuration() 
	: Singleton<Configuration>(), m_description("Server Options")
{
	m_description.add_options()
		("dbusername", "")
		("dbpassword", "")
		("dbhost", "")
		("dbport", "")
		("dbname", "")
		("dbconnectioncount", "")

		("demuxercount", "")

		("characterslots", "")
		("playintro", "")

		("faolanmanageripaddress", "")
		("faolanmanagerport", "")

		("universeagentipaddress", "")
		("universeagentport", "")

		("playeragentipaddress", "")
		("playeragentport", "")
		
		("ipaddress", "")
		("port", "");
}

/*void ShadyOptionsHack(boost::program_options::parsed_options &parsed)
{
	vector<string> to_pass_further = 
		boost::program_options::collect_unrecognized(parsed.options, boost::program_options::include_positional);

	for(int32 i = 0; i < (to_pass_further.size() - 1); i++)
	{
		vector<string> vec;
		//vec.push_back(to_pass_further[i]);
		vec.push_back(to_pass_further[i + 1]);
		parsed.options.push_back(vector<boost::program_options::option>::value_type(to_pass_further[i], vec));
	}
}*/

void Configuration::parseCommandLine(int32 argc, int8 *argv[])
{
	boost::program_options::parsed_options parsed = 
		boost::program_options::command_line_parser(argc, argv).options(m_description).allow_unregistered().run();
	//ShadyOptionsHack(parsed);

	boost::program_options::store(parsed, m_variableMap);
	boost::program_options::notify(m_variableMap);

	if (argc < 2 || (!strcmp(argv[1], "-?") || !strcmp(argv[1], "--?") || !strcmp(argv[1], "/?") || !strcmp(argv[1], "/h") || !strcmp(argv[1], "-h") || !strcmp(argv[1], "--h") || !strcmp(argv[1], "--help") || !strcmp(argv[1], "/help") || !strcmp(argv[1], "-help") || !strcmp(argv[1], "help")))
	{
		printConfiguration();
	}
}

void Configuration::parseConfigFile(const string &configPath)
{
	ifstream ifs(configPath);
	if(ifs.is_open())
	{
		boost::program_options::parsed_options parsed = boost::program_options::parse_config_file(ifs, m_description, true);
		//ShadyOptionsHack(parsed);

		boost::program_options::store(parsed, m_variableMap);
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