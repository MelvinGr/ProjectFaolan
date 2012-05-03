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

#include "Database.h"
#include "Query.h"
#include <iostream>
#include <boost/foreach.hpp>
#include <boost/thread.hpp>

Database::Database(std::size_t poolConnSize) :
m_poolConnSize(poolConnSize), m_shutdown(false), m_dbConn(poolConnSize), m_initialized(false)
{

}

void Database::run()
{

	m_initialized = dbInitialize();
	m_condition.notify_one();
	if(!m_initialized)
	{
		return;
	}


	Query* q;
	while (!m_shutdown)
	{
		m_queryQueue.popWait(q);

		if(m_shutdown && !q)
		{
			// no remaining connection, we can quit the thread
			return;
		}

		DatabaseConnection* dbc;
		m_dbConnQueue.popWait(dbc);
		dbc->processQuery(q);
	}
}

void Database::enqueueQuery(Query* q)
{
	m_queryQueue.push(q);

}

void Database::releaseDBConnection(Database::DatabaseConnection* db)
{
	m_dbConnQueue.push(db);
}

void Database::enqueueFinishedQuery(Query* q)
{
	m_executedQueryQueue.push(q);
}

void Database::runFinishedQueryCallback()
{
	Query* q= NULL;

	m_executedQueryQueue.tryPop(q);
	while (q)
	{
		q->m_callback();
		m_executedQueryQueue.tryPop(q);
	}
}

std::size_t Database::availableDBConnection()
{
	return m_dbConnQueue.size();
}

void Database::shutdown()
{
	m_shutdown=true;

	// send shutdown signal to all connection, and execute last query
	BOOST_FOREACH(DatabaseConnection* dbc, m_dbConn)
	{
		if(dbc)
		{

			dbc->shutdown();
		}

	}

	// all the connections should be shutdown
	m_group.join_all();
	if(m_runThread && m_runThread->joinable())
	{
		m_queryQueue.push(NULL); // null shutdown signal, a bit tricky...
		m_runThread->join();
	}


}

bool Database::executeSynchronousQuery(Query* q)
{
	DatabaseConnection* dbc;
	m_dbConnQueue.popWait(dbc);

	dbc->processSynchronousQuery(q);

	releaseDBConnection(dbc);
	return true;
}



/////////////////////////////////////////////////////////////////
// DatabaseConnection def
/////////////////////////////////////////////////////////////////

Database::DatabaseConnection::DatabaseConnection(Database* db) :
m_db(db), m_connected(false), m_shutdown(false), m_synchronous(false),m_initialized(false), m_query(NULL)
{

}

void Database::DatabaseConnection::processQuery(Query* q)
{
	boost::mutex::scoped_lock lock(m_mutex);

	q->setDatabaseConnection(this);
	m_query=q;
	m_condition.notify_one();
}

void Database::DatabaseConnection::processSynchronousQuery(Query* q)
{

	boost::mutex::scoped_lock lock(m_mutex);

	m_synchronous = true;
	q->setDatabaseConnection(this);
	m_query=q;
	m_condition.notify_one();

	//m_synchronousCond.wait(m_mutex);
	m_synchronous = false;

	// execute now the call back if one
	if (q->m_callbackType==Query::SYNCHRONOUS)
	{
		q->m_callback();
	}

}


void Database::DatabaseConnection::run()
{
	boost::mutex::scoped_lock lock(m_mutex);



	if(!m_initialized)
	{
		return;
	}



	while (!m_query)
	{
		m_condition.wait(lock);
		if (m_query)
		{
			// run query
			if (m_query->m_queryType == Query::HAS_RESULT)
			{
				if (!m_query->storeResult())
				{
					std::cout << m_query->error() <<std::endl;
				}
			}
			else
			{
				if (!m_query->execute())
				{
					std::cout << m_query->error() <<std::endl;
				}
			}


			if(!m_synchronous) // run callback and release connection in asynchronous mode
			{
				if (m_query->m_callbackType==Query::WORKER_THREAD)
				{
					m_query->m_callback();
				} else if (m_query->m_callbackType==Query::MAIN_THREAD)
				{
					m_db->enqueueFinishedQuery(m_query);
				}

				//now release the connection/thread
				m_db->releaseDBConnection(this);

			}
			else
			{
				m_synchronousCond.notify_one();
			}

			m_query=NULL;
		}

		if (m_shutdown)
		{
			return;
		}

	}

}

bool Database::DatabaseConnection::initialized()
{

	boost::mutex::scoped_lock lock(m_mutex);

	return m_initialized;
}
