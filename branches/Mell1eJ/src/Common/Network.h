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

#ifndef NETWORK_H
#define NETWORK_H

#include "Common.h"

#include <boost/asio.hpp>
#include <boost/thread.hpp>
#include <boost/noncopyable.hpp>
#include <boost/bind.hpp>
#include <boost/foreach.hpp>

#include <vector>

#include "Acceptor.h"
#include "ConnectionAcceptor.h"

/**
* Network class
* @author Albator, Doron
*/
class Network : private boost::noncopyable 
{
	vector<Acceptor*> m_acceptors;
	boost::thread_group m_threadGroup;

public:
	Network();

	/**
	* Create a new connection acceptor and run it in a separate thread
	*/
	template<typename ConnectionType> void createConnectionAcceptor(const string& address, const uint32 port,  size_t poolSize)
	{
		ConnectionAcceptor<ConnectionType>* ca = new ConnectionAcceptor<ConnectionType>(address, port, poolSize);
		m_acceptors.push_back(ca);
		m_threadGroup.create_thread(boost::bind(&ConnectionAcceptor<ConnectionType>::run, ca));
	}

	/**
	* shut down all the running acceptor
	*/
	void stop();

	/**
	* Wait acceptors until they stop
	*/
	void wait();

	~Network();
};

#endif