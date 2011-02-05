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

#ifndef VECTOR3D_H
#define VECTOR3D_H

#include "Common.h"

#include <math.h>

class Vector3D
{
public:
	int32 x, y, z;

	Vector3D() {}
	Vector3D(int32 nx, int32 ny, int32 nz)
	{
		x = nx;
		y = ny;
		z = nz;
	}

	ldouble distance(Vector3D otherVector);
};


#endif