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

#ifndef SAFEQUEUE_
#define SAFEQUEUE_

#include "Common.h"

#include <boost/thread.hpp>

#include <queue>

using namespace std;

/**
* A thread safe queue based on Boost::Thread lock
* queue like interface
* @author Albator
*/
template<typename T> class SafeQueue 
{
	queue<T> m_queue;
	mutable boost::mutex m_mutex;
	boost::condition_variable m_condition;

public:
	size_t size()
	{
		boost::mutex::scoped_lock lock(m_mutex);
		return m_queue.size();
	}

	bool empty() const
	{
		boost::mutex::scoped_lock lock(m_mutex);
		return m_queue.size();
	}

	void push(const T& x)
	{
		boost::mutex::scoped_lock lock(m_mutex);
		const bool empty = m_queue.empty();
		m_queue.push(x);

		lock.unlock();

		if (empty)
		{
			m_condition.notify_one();
		}
	}

	T& front()
	{
		boost::mutex::scoped_lock lock(m_mutex);
		return m_queue.front();
	}

	void pop()
	{
		boost::mutex::scoped_lock lock(m_mutex);
		m_queue.pop();
	}

	/**
	* Usefull in the case of a consumer/producer queue.
	* This will wait until a new element is available in the queue
	*/
	void popWait(T& x)
	{
		boost::mutex::scoped_lock lock(m_mutex);
		while (m_queue.empty())
		{
			m_condition.wait(lock);
		}

		x = m_queue.front();
		m_queue.pop();
	}

	void tryPop(T& x)
	{
		boost::mutex::scoped_lock lock(m_mutex);
		if (m_queue.empty())
		{
			x = NULL;
			return;
		}

		x = m_queue.front();
		m_queue.pop();        
	}
};

#endif