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

#ifndef DATABASE_H
#define DATABASE_H

#include "Common.h"

#include <queue>
#include <vector>
#include <iostream>

#include <boost/thread.hpp>
#include <boost/foreach.hpp>
#include <boost/bind.hpp>
#include <boost/function.hpp>
#include <boost/logic/tribool.hpp>
#include <boost/foreach.hpp>
#include <boost/thread.hpp>

#include "SafeQueue.h"

class Query;

/**
* Management of a set of database connexion each executed in a different thread
* The architecture of the following class is close to the ThreadPool pattern.
* ex:
* 1 thread per DB connection (worker threads)
* Database enqueue queries (tasks)
* Tasks can have callback methods executed in the connection thread or in the thread running the database
* @author Albator
*/
class Database 
{
public:
	Database(size_t poolConnSize);

	class DatabaseConnection 
	{
	public:
		DatabaseConnection(Database* db);

		virtual bool connected() = 0;
		virtual void disconnect() = 0;

		virtual ~DatabaseConnection()
		{
		}

		void run();

		/**
		* execute de query asynchronously
		*/
		void processQuery(Query* q);

		void processSynchronousQuery(Query* q);

		Database* m_db;

		virtual void shutdown()=0;

		bool initialized();

		virtual bool dbInitialize() = 0;

		virtual string error() = 0;

	protected:
		mutable boost::mutex m_mutex, m_synchronousMutex, m_initializedMutex;
		boost::condition_variable m_condition, m_synchronousCond, m_initializedCondition;
		bool m_connected, m_shutdown, m_synchronous;

		bool m_initialized;

		Query* m_query;
	};

	/**
	* add a query to the queue
	* executed when a connection thread is available
	*/
	void enqueueQuery(Query* q);

	/**
	* consumed query, can have a callback function executed later in the main thread
	*/
	void enqueueFinishedQuery(Query* q);

	/**
	* Execute the query in synchronous mode
	*/
	bool executeSynchronousQuery(Query* q);
	/**
	* Start database thread and wait until the db is initialized
	*/
	virtual bool start()=0;

	/**
	* Loop through the finished query to execute their callback in the main thread (calling thread)
	* 
	*/
	void runFinishedQueryCallback();

	/**
	* release a connection to the pool
	*/
	void releaseDBConnection(DatabaseConnection* db);

	/**
	* @return number of available DB in the pool
	*/
	size_t availableDBConnection();

	virtual ~Database()
	{

	}

	/**
	* Shutdown database thread and all the thread in the pool
	* delete connections to db
	*/
	void shutdown();

protected:
	virtual bool dbInitialize()=0;

	/**
	* Start all connection threads
	* main database loop, wait for new task.
	*/
	void run();

	SafeQueue<DatabaseConnection*> m_dbConnQueue;
	SafeQueue<Query*> m_queryQueue;
	SafeQueue<Query*> m_executedQueryQueue;
	size_t m_poolConnSize;
	boost::thread_group m_group;
	bool m_shutdown, m_initialized;
	boost::condition_variable m_condition;
	boost::mutex m_mutex;
	vector<DatabaseConnection*> m_dbConn;
	boost::thread *m_runThread;
};
#endif