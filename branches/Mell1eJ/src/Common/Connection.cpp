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

#include "Connection.h"

#include <boost/lexical_cast.hpp>

using namespace std;

Connection::Connection(boost::asio::io_service& IOService, BufferPool *bp) 
	: m_socket(IOService), m_bufferPool(bp), m_readBuffer(2000), connectionCount(0)
{
	//
}

void Connection::start()
{
	gameClient.m_connectionID = connectionCount;
	gameClient.connection = this;
	connectionCount++;	

	printf("New connection accepted (m_connectionID: %u)\n", gameClient.m_connectionID);
	AsyncRead();
}

/*Connection::~Connection()
{
	//
}*/

boost::asio::ip::tcp::socket& Connection::socket()
{
	return m_socket;
}

void Connection::disconnect()
{
	m_socket.shutdown(boost::asio::socket_base::shutdown_both);
}

void Connection::AsyncRead()
{
	m_socket.async_read_some(boost::asio::buffer(m_readBuffer), boost::bind(&Connection::onRead, shared_from_this(),
		boost::asio::placeholders::error, boost::asio::placeholders::bytes_transferred));
}

void Connection::onRead(const boost::system::error_code &e, size_t bytesTransferred)
{
	if(e)
	{
		printf("Client disconnected!\n");
		return;
	}

	if(bytesTransferred == 0)
	{
		AsyncRead();
		return;
	}

	handlePacket(Packet(PacketBuffer(&m_readBuffer[0], bytesTransferred)));
	AsyncRead();
}

void Connection::onWrite(const boost::system::error_code &e)
{
	if (e)
	{
		printf("Socket write error: %s on ConnectionID: %u\n", e.message().c_str(), gameClient.m_connectionID);
		boost::system::error_code ignored_ec;
		m_socket.shutdown(boost::asio::ip::tcp::socket::shutdown_both, ignored_ec);
	}
}

void Connection::AsyncWrite(PacketBuffer &b)
{
	boost::asio::async_write(m_socket, boost::asio::buffer(b.buffer, b.bufferLength), 
		boost::bind(&Connection::onWrite, shared_from_this(), boost::asio::placeholders::error));
}

void Connection::SendPacket(PacketBuffer &b)
{
	b.finalize();
	AsyncWrite(b);
}