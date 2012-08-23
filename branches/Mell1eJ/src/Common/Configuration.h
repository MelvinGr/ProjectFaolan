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

#include <string>
#include <stdexcept>

#include <boost/program_options.hpp>
#include <boost/thread.hpp>
#include <boost/lexical_cast.hpp>

#include "Singleton.h"

#define Config Configuration::Instance()
class FAOLANEXPORTED Configuration : public Singleton<Configuration>
{
	friend class Singleton<Configuration>;

	Configuration();

	boost::program_options::options_description m_description;	
	boost::program_options::variables_map m_variableMap;

public:
	template <typename T> T GetValue(const std::string &other)
	{
		if(m_variableMap.count(other))
			return boost::lexical_cast<T>(m_variableMap[other].as<std::string>());

		throw std::runtime_error("Could not load option: '" + other + "'!");
	}

	void parseCommandLine(int32 argc, const int8* argv[]);
	void parseConfigFile(const std::string &configPath);
	void printConfiguration();
};

#endif
