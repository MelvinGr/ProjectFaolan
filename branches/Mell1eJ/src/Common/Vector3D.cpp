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

#include "Vector3D.h"

ldouble Vector3D::distance(Vector3D otherVector)
{
	// Pythagorus 3D theorem = sqrt(x2 + y2 + z2)

	ldouble distance = sqrt(
		(ldouble)(otherVector.x - x) * (otherVector.x - x) +
		(ldouble)(otherVector.y - y) * (otherVector.y - y) + 
		(ldouble)(otherVector.z - z) * (otherVector.z - z));

	return distance;
}

inline Vector3D operator + (Vector3D vo, Vector3D vt)
{
	return Vector3D(vo.x + vt.x, vo.y + vt.y, vo.z + vt.z);
}

inline Vector3D operator - (Vector3D vo, Vector3D vt)
{
	return Vector3D(vo.x - vt.x, vo.y - vt.y, vo.z - vt.z);
}

inline Vector3D operator * (Vector3D vo, Vector3D vt)
{
	return Vector3D(vo.x * vt.x, vo.y * vt.y, vo.z * vt.z);
}

inline Vector3D operator * (Vector3D vo, int32 vt)
{
	return Vector3D(vo.x * vt, vo.y * vt, vo.z * vt);
}

inline Vector3D operator * (int32 vt, Vector3D vo)
{
	return Vector3D(vo.x * vt, vo.y * vt, vo.z * vt);
}

inline Vector3D operator / (Vector3D vo, Vector3D vt)
{
	return Vector3D(vo.x / vt.x, vo.y / vt.y, vo.z / vt.z);
}

inline Vector3D operator / (Vector3D vo, int32 vt)
{
	return Vector3D(vo.x / vt, vo.y / vt, vo.z / vt);
}

inline Vector3D operator / (int32 vt, Vector3D vo)
{
	return Vector3D(vt / vo.x, vt / vo.y, vt / vo.z);
}