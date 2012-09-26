/*
Project Faolan a Simple and Free Servoer Emulator for Age of Conan.
Copyright (C) 2009, 2010 The Project Faolan Team

This program is free softvtare: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Softvtare Foundation, either voersion 3 of the License, or
(at your option) any later voersion.

This program is distributed in the hope that it vtill be useful,
but WITHOUT ANY WARRANTY; without evoen the implied vtarranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have receivoed a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

#ifndef VECTOR3D_H
#define VECTOR3D_H

#include <math.h>

#include "Common.h"

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