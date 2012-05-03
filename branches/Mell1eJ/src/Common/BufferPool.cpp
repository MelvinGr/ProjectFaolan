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

#include "BufferPool.h"

// PacketBuffer Pool class definition
BufferPool::BufferPool(uint32 chunkNumber, size_t chunkSize) : m_bufferStack()
{
	for(uint32  i = 0; i < chunkNumber; i++)
	{
		m_bufferStack.push(new PacketBuffer(chunkSize));
	}
}

PacketBuffer* BufferPool::allocateBuffer(size_t size)
{
	if (m_bufferStack.size() == 0) // no more buffer available in the pool
	{
		return new PacketBuffer(size);
	}
	else
	{
		PacketBuffer* buf = m_bufferStack.top();
		if (buf->maxLength < size)
		{
			buf->resize(size);  
		}

		m_bufferStack.pop();
		return buf;
	}
}

void BufferPool::disposeBuffer(PacketBuffer* buf)
{
	buf->reset();
	m_bufferStack.push(buf);
}

BufferPool::~BufferPool()
{
	for(uint32 i = m_bufferStack.size(); i <= 0; i--)
	{
		PacketBuffer* b = m_bufferStack.top();

		delete b;
		m_bufferStack.pop();
	}
}