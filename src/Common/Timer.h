/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef TIMER_H
#define TIMER_H

#include "Common.h"

#include <time.h>

#ifdef WINDOWS
    #include <windows.h>
#else
    #include <sys/times.h>
#endif

class Timer
{
private:
	//later convert all combat system and remove it 
	time_t clockSec[3]; //0 start emu: 1 loopdifference: 2 Last_check
	double diff;
	time_t combatSec[3];
	double combatDiff;

	//Stuff for timediff supported by GetTickCount
	uint32 start;
	uint32 CurTime;
	uint32 diffms;

public:
	double attSpeed;
	uint32 LastCheck;

#ifndef WINDOWS
        long GetTickCount()
        {
            tms tm;
            return times(&tm);
        }
#endif

	Timer()
	{
		time(clockSec);
		time(combatSec);
		clockSec[1] = combatSec[1] = 0;
		clockSec[2] = combatSec[2] = 0;
		diff = combatDiff = 0;
	}
	Timer(bool)
	{
		start = GetTickCount();
		CurTime = 0; 
		LastCheck = 0;
		diff = 0;
	}

	//~Timer() { }

	uint32 CalcDelay();
	uint32 Update();

	double CalcCombatDelay();
	uint32 UpdateCombat();
};

#endif