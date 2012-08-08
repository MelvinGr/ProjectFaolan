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

#ifndef BUFFERPOOL_H
#define BUFFERPOOL_H

#include "Common.h"

#include <stack>

#include <boost/foreach.hpp>

#include "PacketBuffer.h"

/*
* BufferPool class to read and write data on raw buffer.
* @author Albator
*/
class BufferPool 
{
	std::stack<PacketBuffer*> m_bufferStack;

public:

	BufferPool(uint32 chunkNumber, size_t chunkSize);

	/**
	* Request a buffer from the pool.
	* This system avoid memory allocation for each packets created and focus on memory reuse
	* These buffers have to be manually released by disposeBuffer.
	* @param size size of the requested buffer in byte.
	*/
	PacketBuffer* allocateBuffer(size_t size);

	/**
	* release a previously requested buffer.
	* This method signal to BufferPool that the space allocated in previously is free again.
	* The buffer cursors are reseted.
	* @param buffer the allocated space to release
	*/
	void disposeBuffer(PacketBuffer* buf);

	/**
	* Delete all buffer in the pool for clean shutdown.
	*/
	~BufferPool();
};

#endif