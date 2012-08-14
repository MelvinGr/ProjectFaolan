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

#ifndef CONNECTION_H
#define CONNECTION_H

#include "Common.h"

#include <map>

#include <boost/asio.hpp>
#include <boost/noncopyable.hpp>
#include <boost/enable_shared_from_this.hpp>
#include <boost/bind.hpp>
#include <boost/asio.hpp>

#include "Packet.h"
#include "PacketBuffer.h"
#include "BufferPool.h"
#include "GameClient.h"

/**
* Abstract connection class
* @author Albator, Doron
*/
class Connection : private boost::noncopyable, public boost::enable_shared_from_this<Connection>
{
public:
	Connection(boost::asio::io_service& IOService, BufferPool* bp);
	//~Connection();

	boost::asio::ip::tcp::socket& socket();

	// First operation executed after establishing a connection
	void start();

	uint32 connectionCount;
	GameClient gameClient;

	void SendPacket(PacketBuffer &b);

protected:
	boost::asio::ip::tcp::socket m_socket;
	BufferPool* m_bufferPool;
	std::vector<uint8> m_readBuffer; 

	void disconnect();

	void onRead(const boost::system::error_code& e, size_t bytesTransferred);
	void onWrite(const boost::system::error_code& e);

	virtual void handlePacket(Packet &packet) = 0;

	void AsyncRead();
	void AsyncWrite(PacketBuffer &b);
};

#endif