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

#include "MysqlDatabase.h"

MysqlDatabase* MysqlDatabase::m_db = NULL;

MysqlDatabase::MysqlDatabase(size_t poolSize, const string& login, const string& host, const string& password, const string& database, uint32 port) :
	Database(poolSize), m_login(login), m_host(host), m_password(password), m_database(database), m_port(port)
{

}

MysqlDatabase* MysqlDatabase::createInstance(size_t poolSize, const string &login, const string &host, const string &password, const string &database, uint32 port)
{
	return (m_db ? m_db : new MysqlDatabase(poolSize, login, host, password, database, port));
}

MysqlDatabase* MysqlDatabase::instance()
{
	return m_db;
}

void MysqlDatabase::destroy()
{
	if(m_db)
	{
		m_db->shutdown();
		delete m_db;
	}
}

bool MysqlDatabase::dbInitialize()
{
	for (uint32 i=0; i < m_poolConnSize; i++)
	{
		DatabaseConnection* dbc = new MysqlDatabaseConnection(this, m_login, m_host, m_password, m_database, m_port);
		m_dbConnQueue.push(dbc);
		m_dbConn.push_back(dbc);

		if(!dbc->dbInitialize())
		{
			printf("%s\n", dbc->error().c_str());
			return false;
		}

		m_group.create_thread(boost::bind(&MysqlDatabaseConnection::run, dbc));
	}

	return true;
}

bool MysqlDatabase::start()
{
	boost::mutex::scoped_lock lock(m_mutex);

	m_runThread = new boost::thread(boost::bind(&MysqlDatabase::run, this));
	m_condition.wait(lock);	

	if(m_initialized)
	{
		printf("Connected to Mysql DB on port: %d Host: %s DB name: %s \n", m_port, m_host.c_str(), m_database.c_str());
	}
	else
	{
		printf("Fail to connect to Mysql DB on port: %d Host: %s DB name: %s \n", m_port, m_host.c_str(), m_database.c_str());
	}

	return m_initialized;
}

MysqlDatabase::~MysqlDatabase()
{
	BOOST_FOREACH(DatabaseConnection* dbc, m_dbConn)
	{
		if(dbc)
		{
			delete dbc;
		}
	}

	if (m_runThread)
	{
		delete m_runThread;
	}
}

// MysqlDatabaseConnection def
MysqlDatabase::MysqlDatabaseConnection::MysqlDatabaseConnection(Database* db, const string& login, const string& host, const string& password, const string& database, uint32 port) :
	DatabaseConnection(db), m_login(login), m_host(host), m_password(password), m_database(database), m_port(port)
{

}

bool MysqlDatabase::MysqlDatabaseConnection::dbInitialize()
{
	mysql_init(&m_mysql);
	//mysql_option(&m_mysql, MYSQL_SET_CHARSET_NAME, "utf8");

	if (!mysql_real_connect(&m_mysql, m_host.c_str(), m_login.c_str(), m_password.c_str(), m_database.c_str(), m_port, NULL, 0))
	{
		m_initialized = false;
	}

	return (m_initialized = connected());
}

bool MysqlDatabase::MysqlDatabaseConnection::connected()
{
	return !mysql_ping(&m_mysql);
}

void MysqlDatabase::MysqlDatabaseConnection::disconnect()
{
	mysql_close(&m_mysql);
}

MysqlDatabase::MysqlDatabaseConnection::~MysqlDatabaseConnection()
{
	disconnect();
}

void MysqlDatabase::MysqlDatabaseConnection::shutdown()
{
	m_shutdown = true;
	m_condition.notify_one();
}

string MysqlDatabase::MysqlDatabaseConnection::error()
{
	return mysql_error(&m_mysql);
}