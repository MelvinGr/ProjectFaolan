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

#ifndef MYSQLDATABASE_H
#define MYSQLDATABASE_H

#include "Common.h"

#include <boost/thread.hpp>
#include <boost/foreach.hpp>

#include <mysql/mysql.h>

#include "Database.h"
#include "Singleton.h"

#define MySQLDB MysqlDatabase::instance()
class MysqlDatabase : public Database 
{
	MysqlDatabase(size_t poolSize, const string& login, const string& host, const string& password, const string& database, uint32 port);

	~MysqlDatabase();

	string m_login, m_host, m_password, m_database;
	const uint32 m_port;
	bool dbInitialize();

	static MysqlDatabase* m_db;

public:
	static MysqlDatabase* createInstance(size_t poolSize, const string& login, const string& host, const string& password, const string& database, uint32 port);

	static MysqlDatabase* instance();
	static void destroy();

	bool start();

	class MysqlDatabaseConnection : public Database::DatabaseConnection 
	{
	private:
		bool dbInitialize();
		MYSQL *m_mysql;

		string m_login, m_host, m_password, m_database;
		const uint32 m_port;

	public:
		friend class MysqlQuery;
		MysqlDatabaseConnection(Database* db, const string& login, const string& host, const string& password, const string& database, uint32 port);
		~MysqlDatabaseConnection();

		bool connected();

		void disconnect();
		void shutdown();

		string error();
	};
};

#endif