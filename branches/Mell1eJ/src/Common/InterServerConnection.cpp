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

#include "InterServerConnection.h"

#include <boost/bind.hpp>
#include <boost/lexical_cast.hpp>

using namespace std;

InterServerConnection::InterServerConnection(boost::asio::io_service& IOService, 
											 const std::string& loginServerAddress, uint32 port, FaolanManagerSenderReceivers _sender)
											 : m_IOService(IOService),m_Socket(IOService),m_Resolver(IOService), m_readBuffer(1000), m_writeBuffer(1000),
											 m_loginServerAddress(loginServerAddress), m_port(port), m_connected(false), m_registered(false), sender(_sender)
{
	//
}

void InterServerConnection::start()
{
	boost::asio::ip::tcp::resolver::query query(m_loginServerAddress, boost::lexical_cast<string>(m_port));

	m_Resolver.async_resolve(query,
		boost::bind(&InterServerConnection::onResolve, this,
		boost::asio::placeholders::error,
		boost::asio::placeholders::iterator));

	m_IOService.run();
}

void InterServerConnection::stop()
{
	m_condition.notify_all();

	m_Socket.close();
	m_IOService.stop();
}

void InterServerConnection::onResolve(const boost::system::error_code& err, boost::asio::ip::tcp::resolver::iterator endpoint_iterator)
{
	if (!err)
	{
		// Attempt a connection to the first endpoint in the list. Each endpoint
		// will be tried until we successfully establish a connection.
		boost::asio::ip::tcp::endpoint endpoint = *endpoint_iterator;

		m_Socket.async_connect(endpoint, boost::bind(&InterServerConnection::onConnect, this,
			boost::asio::placeholders::error, ++endpoint_iterator));
	}
	else
	{
		printf("onResolve:InterServerConnection error: %s\n",err.message().c_str());
		m_condition.notify_all();
	}
}

void InterServerConnection::onConnect(const boost::system::error_code& err, boost::asio::ip::tcp::resolver::iterator endpoint_iterator)
{
	if (!err)
	{
		m_connected = true;
		//registerWithManager();
		m_registered = true;
		m_condition.notify_all();

		AsyncRead();
	}
	else if (endpoint_iterator != boost::asio::ip::tcp::resolver::iterator())
	{
		// The connection failed. Try the next endpoint in the list.
		m_Socket.close();
		boost::asio::ip::tcp::endpoint endpoint = *endpoint_iterator;
		m_Socket.async_connect(endpoint, boost::bind(&InterServerConnection::onConnect, this,
			boost::asio::placeholders::error, ++endpoint_iterator));
	}
	else
	{
		printf("onConnect:InterServerConnection error: %s\n",err.message().c_str());
		m_condition.notify_all();
	}
}

void InterServerConnection::onRead(const boost::system::error_code& e, std::size_t bytesTransferred)
{
	if(e)
	{
		stop();
		return;
	}

	if(bytesTransferred == 0)
	{
		AsyncRead();
		return;
	}

	Packet packet(PacketBuffer(&m_readBuffer[0], bytesTransferred));
	//if(packet.opcode == RequestRegisterResponse)
	//	onRegistrationStatus(packet);
	//else
	HandlePacketCallback(packet);

	AsyncRead();
}

void InterServerConnection::onWrite(const boost::system::error_code &e)
{
	if (e)
	{
		printf("writeerror\n");
		printf("InterServerCommunication: socket write problem \n");
		boost::system::error_code ignored_ec;
		m_Socket.shutdown(boost::asio::ip::tcp::socket::shutdown_both, ignored_ec);
	}
}

bool InterServerConnection::waitUntilConnected()
{
	boost::mutex::scoped_lock lock(m_mutex);
	m_condition.wait(lock);

	return (m_connected && m_registered);
}

void InterServerConnection::AsyncRead()
{
	m_Socket.async_read_some(boost::asio::buffer(m_readBuffer), boost::bind(&InterServerConnection::onRead, this,
		boost::asio::placeholders::error, boost::asio::placeholders::bytes_transferred));
}

void InterServerConnection::AsyncWrite(PacketBuffer& b)
{
	boost::asio::async_write(m_Socket, boost::asio::buffer(b.buffer, b.bufferLength),
		boost::bind(&InterServerConnection::onWrite, this, boost::asio::placeholders::error));
}

void InterServerConnection::SendPacket(PacketBuffer &b)
{
	b.finalize();
	AsyncWrite(b);
}