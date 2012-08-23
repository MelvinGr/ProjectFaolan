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

#ifndef MYSQLQUERY_H
#define MYSQLQUERY_H

#include "Query.h"
#include "Common.h"
#include "MysqlDatabase.h"
#include <map>
#include <string>

class FAOLANEXPORTED MysqlQuery : public Query 
{
	MYSQL_RES* m_res;
	MYSQL_ROW m_row;

public:
	// Query with a callback
	MysqlQuery(boost::function<void ()> f, CallbackType type, QueryType t);
	MysqlQuery(CallbackType type, QueryType t);

	// Query without callback
	MysqlQuery(QueryType t);

	bool execute();
	bool fetchRow();
	uint64 numRows();

	bool storeResult();
	bool succes();

	std::string error();

	const char* getRealmName(uint32 idx);
	uint32 getRealmId();

	bool nextRow();
	const char* getCharString(uint32 idx);
	uint32 getCharUint(uint32 idx);

	uint32 getUint32();
	uint64 getUint64();
	double getDouble();
	const char* getString();

	uint32 getUint32(uint32 idx);
	uint64 getUint64(uint32 idx);
	double getDouble(uint32 idx);
	const char* getString(uint32 idx);
};

#endif
