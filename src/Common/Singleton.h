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

#ifndef SINGLETON_H
#define SINGLETON_H

#include <assert.h>

#define InitialiseSingleton(type) template<> type* Singleton<type>::singleton = 0

// To be used as a replacement for initialiseSingleton()
// Creates a file-scoped Singleton object, to be retrieved with getSingleton
#define CreateFileSingleton(type) \
	InitialiseSingleton(type); \
	type the##type

template<class type> class Singleton
{
protected:
	static type* singleton;

public:
	Singleton()
	{
		assert(this->singleton == 0);
		this->singleton = static_cast<type*>(this);
	}

	virtual ~Singleton() 
	{
		this->singleton = 0;
	}

	inline static type& getSingleton() 
	{
		assert(singleton); 
		return *singleton; 
	}

	inline static type* getSingletonPtr() 
	{
		return singleton;
	}
};
#endif