/*
* ArcEmu MMORPG Server
* Copyright (C) 2008-2010 <http://www.ArcEmu.org/>
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU Affero General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU Affero General Public License for more details.
*
* You should have received a copy of the GNU Affero General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
*
*/

#ifndef MUTEX_H
#define MUTEX_H

#include "Common.h"

#ifdef WINDOWS
#include <windows.h>
#else
#include <pthread.h>
#endif

class Mutex
{
public:
	friend class Condition;

	// Initializes a mutex class, with InitializeCriticalSection / pthread_mutex_init
	Mutex();

	// Deletes the associated critical section / mutex
	virtual ~Mutex();

	// Acquires this mutex. If it cannot be acquired immediately, it will block.
	inline void Acquire()
	{
#ifndef WIN32
		pthread_mutex_lock(&mutex);
#else
		EnterCriticalSection(&cs);
#endif
	}

	// Releases this mutex. No error checking performed
	inline void Release()
	{
#ifndef WIN32
		pthread_mutex_unlock(&mutex);
#else
		LeaveCriticalSection(&cs);
#endif
	}

	/* Attempts to acquire this mutex. If it cannot be acquired (held by another thread)
	* it will return false.
	* @return false if cannot be acquired, true if it was acquired. */	
	inline bool AttemptAcquire()
	{
#ifndef WIN32
		return (pthread_mutex_trylock(&mutex) == 0);
#else
		return (TryEnterCriticalSection(&cs) == TRUE ? true : false);
#endif
	}

protected:
#ifdef WIN32
	// Critical section used for system calls
	CRITICAL_SECTION cs;

#else
	// Static mutex attribute
	static bool attr_initalized;
	static pthread_mutexattr_t attr;

	// pthread struct used in system calls
	pthread_mutex_t mutex;
#endif
};

#ifdef WIN32

class FastMutex
{
#pragma pack(push, 8)
	volatile long m_lock;
#pragma pack(pop)
	DWORD m_recursiveCount;

public:
	inline FastMutex() : m_lock(0),m_recursiveCount(0) {}
	inline ~FastMutex() {}

	inline void Acquire()
	{
		DWORD thread_id = GetCurrentThreadId(), owner;
		if(thread_id == (DWORD)m_lock)
		{
			++m_recursiveCount;
			return;
		}

		while(true)
		{
			owner = InterlockedCompareExchange(&m_lock, thread_id, 0);
			if(owner == 0)
				break;

			Sleep(0);
		}

		++m_recursiveCount;
	}

	inline bool AttemptAcquire()
	{
		DWORD thread_id = GetCurrentThreadId();
		if(thread_id == (DWORD)m_lock)
		{
			++m_recursiveCount;
			return true;
		}

		DWORD owner = InterlockedCompareExchange(&m_lock, thread_id, 0);
		if(owner == 0)
		{
			++m_recursiveCount;
			return true;
		}

		return false;
	}

	inline void Release()
	{
		if((--m_recursiveCount) == 0)
			InterlockedExchange(&m_lock, 0);
	}
};

#else
#define FastMutex Mutex

#endif
#endif