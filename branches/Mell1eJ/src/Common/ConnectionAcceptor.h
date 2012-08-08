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

#ifndef CONNECTIONACCEPTOR_H
#define CONNECTIONACCEPTOR_H

#include "Common.h"

#include <boost/asio.hpp>
#include <boost/noncopyable.hpp>
#include <boost/shared_ptr.hpp>
#include <boost/bind.hpp>
#include <boost/foreach.hpp>
#include <boost/lexical_cast.hpp>

#include <vector>

#include "Acceptor.h"
#include "BufferPool.h"
#include "Functions.h"

/**
* A generic connection acceptor using a specified number of io_service
* @author Albator, Doron
*/
template <typename T> class ConnectionAcceptor : public Acceptor
{
	boost::asio::ip::tcp::acceptor* m_acceptor;   
	std::vector< std::pair<boost::asio::io_service*, BufferPool*> > m_IOServices;
	std::vector<boost::asio::io_service::work*> m_works;

	boost::shared_ptr<T> m_connection;
	uint32 m_nextIOService;

	/**
	* event call when there is a new connection attempt
	*/
	void onAccept(const boost::system::error_code& e)
	{
		if (!e)
		{
			m_connection->start();
			std::pair<boost::asio::io_service*, BufferPool*> demux = IOService();
			m_connection.reset(new T(*(demux.first), demux.second));
			m_acceptor->async_accept(m_connection->socket(), boost::bind(
				&ConnectionAcceptor::onAccept, this, 
				boost::asio::placeholders::error));
		}
	}

	/**
	* return next available io_service using a round robin for load balancing
	*/
	std::pair<boost::asio::io_service*, BufferPool*> IOService()
	{
		std::pair<boost::asio::io_service*, BufferPool*> ioS = m_IOServices[m_nextIOService];
		++m_nextIOService;
		if (m_nextIOService >= m_IOServices.size())
		{
			m_nextIOService = 0;
		}

		return ioS;
	}

public:  
	ConnectionAcceptor(const std::string& address, const uint32 port, size_t poolSize)
		: m_connection(), m_nextIOService(0)
	{
		for (uint32 i=0; i<poolSize; i++)
		{
			boost::asio::io_service* s = new boost::asio::io_service();
			boost::asio::io_service::work* w = new boost::asio::io_service::work(*s);
			BufferPool* bp = new BufferPool(20, 1000);
			m_IOServices.push_back(std::make_pair(s, bp));
			m_works.push_back(w);
		}

		m_acceptor = new boost::asio::ip::tcp::acceptor(*(IOService().first));
		boost::asio::ip::tcp::resolver resolver(m_acceptor->get_io_service()); //io_service
		boost::asio::ip::tcp::resolver::query query(address, boost::lexical_cast<std::string>(port));
		boost::asio::ip::tcp::endpoint endpoint = *resolver.resolve(query);
		m_acceptor->open(endpoint.protocol());
		m_acceptor->set_option(boost::asio::ip::tcp::acceptor::reuse_address(true));
		m_acceptor->bind(endpoint);
		m_acceptor->listen();

		std::pair<boost::asio::io_service*, BufferPool*> demux = IOService();
		m_connection.reset(new T(*(demux.first), demux.second));
		m_acceptor->async_accept(m_connection->socket(), boost::bind(
			&ConnectionAcceptor::onAccept, this, boost::asio::placeholders::error));

		printf("Server listening on port: %u \n", port);
	}


	/**
	* Run until stop is called or all the io_service return
	* 
	*/
	void run()
	{

		std::vector<boost::shared_ptr<boost::thread> > threads;
		for (size_t i = 0; i < m_IOServices.size(); ++i)
		{
			boost::shared_ptr<boost::thread> thread(new boost::thread(
				boost::bind(&boost::asio::io_service::run, m_IOServices[i].first)));
			threads.push_back(thread);

		}

		for (size_t i = 0; i < threads.size(); ++i)
			threads[i]->join();
	}


	/**
	* stop all the running io_services
	*/
	void stop()
	{
		for (size_t i = 0; i < m_IOServices.size(); ++i)
			m_IOServices[i].first->stop();
	}


	~ConnectionAcceptor()
	{
		BOOST_FOREACH(boost::asio::io_service::work* w, m_works)
		{
			delete w;
		}

		//BOOST_FOREACH(pair<boost::asio::io_service*, BufferPool*>& ioS, m_IOServices)
		//{
		//delete ioS;
		//	}

		delete m_acceptor;
	} 
};

#endif 