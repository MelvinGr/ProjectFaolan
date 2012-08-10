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

#ifndef MYSQLDATABASE_H
#define MYSQLDATABASE_H

#include "Common.h"
#include "Database.h"
#include "Singleton.h"

#if PLATFORM == PLATFORM_WIN32
#	include <winsock2.h>
#endif
#include <mysql/mysql.h>

#define MysqlDB MysqlDatabase::Instance()
class MysqlDatabase : public Database
{
	MysqlDatabase(std::size_t poolSize, const std::string& login,
		const std::string& host, const std::string& password,
		const std::string& database, uint32 port);

	~MysqlDatabase();

	std::string m_login, m_host, m_password, m_database;
	const uint32 m_port;
	bool dbInitialize();

	static MysqlDatabase* m_db;

public:
	static void createInstance(std::size_t poolSize, const std::string& login,
		const std::string& host, const std::string& password,
		const std::string& database, uint32 port);

	static MysqlDatabase* Instance();

	static void destroy();
	bool start();

	class MysqlDatabaseConnection : public Database::DatabaseConnection 
	{
		bool dbInitialize();
		MYSQL m_mysql;

		std::string m_login, m_host, m_password, m_database;
		const uint32 m_port;

	public:
		friend class MysqlQuery;
		MysqlDatabaseConnection(Database* db, const std::string& login,
			const std::string& host, const std::string& password,
			const std::string& database, uint32 port);

		bool connected();
		bool disconnect();
		void shutdown();

		~MysqlDatabaseConnection();

		std::string error();
	};
};

#endif
