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

#ifndef InterServerConnection_H
#define InterServerConnection_H

#include "Common.h"

#include <boost/asio.hpp>
#include <boost/thread.hpp>

#include "FaolanManager.h"
#include "PacketBuffer.h"
#include "Packet.h"

/**
* Connection to the auth server to register the char server
* @author Albator
*/
class FAOLANEXPORTED InterServerConnection
{
//protected:
	// synchro
	mutable boost::mutex m_mutex;
	boost::condition_variable m_condition;

	void onResolve(const boost::system::error_code& err, boost::asio::ip::tcp::resolver::iterator endpoint_iterator);
	void onConnect(const boost::system::error_code& err, boost::asio::ip::tcp::resolver::iterator endpoint_iterator);

	void onRead(const boost::system::error_code& e, std::size_t bytesTransferred);
	void onWrite(const boost::system::error_code& e);

	void AsyncRead();
	void AsyncWrite(PacketBuffer& b);

	boost::asio::io_service& m_IOService;
	boost::asio::ip::tcp::socket m_Socket;
	boost::asio::ip::tcp::resolver m_Resolver;

	std::vector<uint8> m_readBuffer, m_writeBuffer;

	const std::string& m_loginServerAddress;
	uint32 m_port;

	bool m_connected, m_registered;

	FaolanManagerSenderReceivers sender;

public:
	InterServerConnection(boost::asio::io_service& IOService, const std::string& loginServerAddress, 
		uint32 port, FaolanManagerSenderReceivers _sender);

	boost::function<void(Packet&)> HandlePacketCallback;

	/**
	* Main function of inter server communication process.
	* This has to be run inside a different thread.
	* This will run until a call to stop() or an error.
	*/
	void start();	
	// Stop the connection
	void stop();

	// blocking function to wait the connection to the login server.
	bool waitUntilConnected();

	void SendPacket(PacketBuffer &b);
};

#endif