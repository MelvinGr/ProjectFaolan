/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011 The Project Faolan Team

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

	C++ Big Integer Library
http://mattmccutchen.net/bigint/
*/

#ifndef BIGINTEGERUTILS_H
#define BIGINTEGERUTILS_H

#include "BigInteger.h"
#include <string>
#include <iostream>

using namespace std;

/* This file provides:
 * - Convenient string <-> BigUnsigned/BigInteger conversion routines
 * - ostream << operators for BigUnsigned/BigInteger */

// string conversion routines.  Base 10 only.
string bigUnsignedToString(const BigUnsigned &x);
string bigIntegerToString(const BigInteger &x);
BigUnsigned stringToBigUnsigned(const string &s);
BigInteger stringToBigInteger(const string &s);

// Creates a BigInteger from data such as `char's; read below for details.
template <class T>
BigInteger dataToBigInteger(const T* data, BigInteger::Index length, BigInteger::Sign sign);

// Outputs x to os, obeying the flags `dec', `hex', `bin', and `showbase'.
ostream &operator <<(ostream &os, const BigUnsigned &x);

// Outputs x to os, obeying the flags `dec', `hex', `bin', and `showbase'.
// My somewhat arbitrary policy: a negative sign comes before a base indicator (like -0xFF).
ostream &operator <<(ostream &os, const BigInteger &x);

// BEGIN TEMPLATE DEFINITIONS.

/*
 * Converts binary data to a BigInteger.
 * Pass an array `data', its length, and the desired sign.
 *
 * Elements of `data' may be of any type `T' that has the following
 * two properties (this includes almost all integral types):
 *
 * (1) `sizeof(T)' correctly gives the amount of binary data in one
 * value of `T' and is a factor of `sizeof(Blk)'.
 *
 * (2) When a value of `T' is casted to a `Blk', the low bytes of
 * the result contain the desired binary data.
 */
template <class T>
BigInteger dataToBigInteger(const T* data, BigInteger::Index length, BigInteger::Sign sign) {
	// really ceiling(numBytes / sizeof(BigInteger::Blk))
	unsigned int pieceSizeInBits = 8 * sizeof(T);
	unsigned int piecesPerBlock = sizeof(BigInteger::Blk) / sizeof(T);
	unsigned int numBlocks = (length + piecesPerBlock - 1) / piecesPerBlock;

	// Allocate our block array
	BigInteger::Blk *blocks = new BigInteger::Blk[numBlocks];

	BigInteger::Index blockNum, pieceNum, pieceNumHere;

	// Convert
	for (blockNum = 0, pieceNum = 0; blockNum < numBlocks; blockNum++) {
		BigInteger::Blk curBlock = 0;
		for (pieceNumHere = 0; pieceNumHere < piecesPerBlock && pieceNum < length;
			pieceNumHere++, pieceNum++)
			curBlock |= (BigInteger::Blk(data[pieceNum]) << (pieceSizeInBits * pieceNumHere));
		blocks[blockNum] = curBlock;
	}

	// Create the BigInteger.
	BigInteger x(blocks, numBlocks, sign);

	delete [] blocks;
	return x;
}

#endif
