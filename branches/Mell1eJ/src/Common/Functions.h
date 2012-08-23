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

#ifndef FUNCTIONS_H
#define FUNCTIONS_H

#include "Common.h"

#include <string>

namespace String
{
	FAOLANEXPORTED std::string arrayToHexString(const uint8* data, size_t size);
	FAOLANEXPORTED std::string serverInfoStr();
	FAOLANEXPORTED std::string replace(const std::string& str, const std::string& from, const std::string& to);
	FAOLANEXPORTED std::string timeString();
}     

namespace File
{
	//uint64 Read(const std::string& path, bool binary, uint8 **buffer);
	//void Write(const std::string& path, bool binary, const uint8 *buffer, size_t length);

	FAOLANEXPORTED void Read(const std::string& path, std::vector<uint8> &buffer);
	FAOLANEXPORTED void Write(const std::string& path, const std::vector<uint8> &buffer);

	FAOLANEXPORTED bool Exists(const std::string& strFilename);
}

#endif