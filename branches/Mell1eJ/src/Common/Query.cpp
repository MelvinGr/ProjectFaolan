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

#include "Query.h"

Query::Query(boost::function<void ()> f, CallbackType type, QueryType t) : m_callback(f), m_callbackType(type), m_queryType(t), m_dbc(NULL), m_idx(0)
{

}

Query::Query(QueryType t) : m_callbackType(Query::NO_CALLBACK), m_queryType(t), m_dbc(NULL), m_idx(0)
{

}

Query::Query(CallbackType type, QueryType t) : m_callbackType(type), m_queryType(t), m_dbc(NULL), m_idx(0)
{

}

void Query::setDatabaseConnection(Database::DatabaseConnection* dbc)
{
	m_dbc=dbc;
}

void Query::setQueryText(const char* queryTxt, ...)
{
	va_list Params;
	va_start(Params, queryTxt);
	vsprintf(m_queryTxt, queryTxt, Params);
	va_end(Params);
}

void Query::releaseDBConnection()
{
	if (m_dbc)
	{
		Database* db = m_dbc->m_db;
		db->releaseDBConnection(m_dbc);
	}
}

void Query::setCallbackFunction(boost::function<void ()> f)
{
	m_callback=f;
}

void Query::setCallbackType(CallbackType type)
{
	m_callbackType=type;
}