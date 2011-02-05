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

#ifndef ACCEPTOR_H
#define ACCEPTOR_H

#include "Common.h"

#include <boost/asio.hpp>
#include <boost/noncopyable.hpp>

#include "BufferPool.h"

class Acceptor : private boost::noncopyable
{    
	/**
	* event call when there is a new connection attempt
	*/
	virtual void onAccept(const boost::system::error_code& e)=0;

	/**
	* return next available io_service using a round robin for load balancing
	*/
	virtual pair<boost::asio::io_service*, BufferPool*> IOService()=0;

public:
	/**
	* Run until stop is called or all the io_service return
	* 
	*/
	virtual void run()=0;

	/**
	* stop all the running io_services
	*/
	virtual void stop()=0;

	virtual ~Acceptor()
	{
	}

};
#endif
