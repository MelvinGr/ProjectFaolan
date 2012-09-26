/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

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

#include "Timer.h"
uint32 Timer::CalcDelay()
{
	CurTime = GetTickCount();
	diff = CurTime - start;
	start += diff;
	LastCheck += diff;

	return diff;
}

uint32 Timer::Update()
{
	return CalcDelay();
}

double Timer::CalcCombatDelay()
{
	time(&combatSec[1]);
	combatDiff = difftime(combatSec[1], combatSec[0]);

	if(combatDiff >= attSpeed)
		combatSec[0] += combatDiff;

	return combatDiff;
}

uint32 Timer::UpdateCombat()
{
	return CalcCombatDelay();
}