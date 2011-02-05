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

#include "MysqlQuery.h"

MysqlQuery::MysqlQuery(QueryType t) : Query(t), m_res(NULL)
{

}

MysqlQuery::MysqlQuery(boost::function<void ()> f, CallbackType type, QueryType t) : Query(f, type, t), m_res(NULL)
{

}

MysqlQuery::MysqlQuery(CallbackType type, QueryType t) : Query(type, t), m_res(NULL)
{

}

bool MysqlQuery::execute()
{
	if (m_dbc)
	{
		MysqlDatabase::MysqlDatabaseConnection* mydbc = (MysqlDatabase::MysqlDatabaseConnection*)m_dbc;

		if (!mysql_query(mydbc->m_mysql, m_queryTxt))
		{
			return true;
		}
	}

	return false;
}

bool MysqlQuery::storeResult()
{
	if (execute() && !m_res)
	{
		MysqlDatabase::MysqlDatabaseConnection* mydbc = (MysqlDatabase::MysqlDatabaseConnection*)m_dbc;
		m_res = mysql_store_result(mydbc->m_mysql);
		if (m_res)
		{
			m_column = mysql_num_fields(m_res);
			return true;
		}
	}

	return false;
}

bool MysqlQuery::fetchRow()
{
	if (m_res)
	{
		m_row = mysql_fetch_row(m_res);
		if (m_row)
		{
			return true;
		}
	}

	return false;
}

uint64 MysqlQuery::numRows()
{
	if(m_res)
	{
		return mysql_num_rows(m_res);
	}
	else
	{
		return 0;
	}
}

string MysqlQuery::error()
{
	if (m_dbc)
	{
		MysqlDatabase::MysqlDatabaseConnection* mydbc = (MysqlDatabase::MysqlDatabaseConnection*)m_dbc;
		return mysql_error(mydbc->m_mysql);
	}

	return "";
}

bool MysqlQuery::succes()
{
	if (m_dbc)
	{
		MysqlDatabase::MysqlDatabaseConnection* mydbc = (MysqlDatabase::MysqlDatabaseConnection*)m_dbc;
		return (mysql_errno(mydbc->m_mysql) == 0);
	}
	else 
		return false;
}

// get returned values from query
uint32 MysqlQuery::getUint32(uint32 idx)
{
	if (m_res && m_row && (idx < m_column))
	{
		m_idx++;
		return atoi(m_row[idx]);
	}

	return NULL;
}

bool MysqlQuery::nextRow()
{
	m_row=mysql_fetch_row(m_res);
	if(m_row<0)
		return false;
	else
		return true;
}

const char* MysqlQuery::getCharString(uint32 idx)
{
	return m_row[idx];
}

uint32 MysqlQuery::getCharUint(uint32 idx)
{
	return boost::lexical_cast<uint32>(m_row[idx]);
}

uint64 MysqlQuery::getUint64(uint32 idx)
{
	if (m_res && m_row && (idx < m_column))
	{
		m_idx++;
		return boost::lexical_cast<uint64>(m_row[idx]);
	}

	return NULL;
}

const char* MysqlQuery::getString(uint32 idx)
{
	if (m_res && m_row && (idx < m_column))
	{
		m_idx++;
		return m_row[idx];
	}

	return NULL;
}

uint32 MysqlQuery::getUint32()
{
	return getUint32(m_idx);
}

uint64 MysqlQuery::getUint64()
{
	return getUint64(m_idx);
}

const char* MysqlQuery::getString()
{
	return getString(m_idx);
}