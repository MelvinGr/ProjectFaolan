/*
Copyright (c) 2005-2009, CellAO Team

All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
* Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, 
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, 
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

#include "LoginEncryption.h"

#include <iostream>

#include "BigIntegerLibrary/BigIntegerLibrary.h"

#include "SwapByte.h"
#include "Functions.h"

using namespace std;

string LoginEncryption::decryptLoginKey(const string& loginKey)
{
	BigUnsigned modulus(BigUnsignedInABase("eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f", 16));
	BigUnsigned serverPrivateKey(BigUnsignedInABase("7ad852c6494f664e8df21446285ecd6f400cf20e1d872ee96136d7744887424b", 16));
	stringstream keyStream(loginKey);

	string clientPublicKeyString;
	getline(keyStream, clientPublicKeyString, '-');

	if (clientPublicKeyString == "")
		return "";

	BigUnsigned clientPublicKey = BigUnsignedInABase(clientPublicKeyString, 16);

	string encryptedBlock;
	getline(keyStream, encryptedBlock);

	if (encryptedBlock == "")
		return "";

	string teaKey = BigUnsignedInABase(modexp(clientPublicKey, serverPrivateKey, modulus), 16);

	// Pad or truncate to 128-bit as needed
	if (teaKey.length() < 32)
		teaKey.insert(teaKey.begin(), 32 - teaKey.length(), '0');
	else
		teaKey.resize(32);

	stringstream teaKeyStream(teaKey);
	uint32 key[4];
	key[0] = popHexInt(teaKeyStream);
	key[1] = popHexInt(teaKeyStream);
	key[2] = popHexInt(teaKeyStream);
	key[3] = popHexInt(teaKeyStream);

	// <8 Bytes data> <string length> <string>
	string dataString = decryptTea(encryptedBlock, key);
	uint32 length = *(reinterpret_cast<uint32*>(&dataString[8]));
	SwapByte::Swap<uint32>(length);

	return dataString.substr(12, 12 + length);
}

uint8 LoginEncryption::popHexChar(stringstream& sstr)
{
	int8 ch1, ch2;
	sstr >> ch1 >> ch2;

	uint8 dig1, dig2;

	if (isdigit(ch1)) dig1 = ch1 - '0';
	else if (ch1 >= 'A' && ch1 <= 'F') dig1 = ch1 - 'A' + 10;
	else if (ch1 >= 'a' && ch1 <= 'f') dig1 = ch1 - 'a' + 10;

	if (isdigit(ch2)) dig2 = ch2 - '0';
	else if (ch2 >= 'A' && ch2 <= 'F') dig2 = ch2 - 'A' + 10;
	else if (ch2 >= 'a' && ch2 <= 'f') dig2 = ch2 - 'a' + 10;

	return dig1 * 16 + dig2;
}

uint32 LoginEncryption::popHexInt(stringstream& sstr)
{
	uint32 accumulator = 0;

	for (uint32 i = 0; i < 4; i++)
		accumulator += popHexChar(sstr) << (8 * i);

	return accumulator;
}

string LoginEncryption::decryptTea(const string& encryptedBlock, const uint32* key)
{
	uint32 dataBlock[2];
	uint32 prev[2];
	uint32 newPrev[2];
	uint8* p = reinterpret_cast<uint8*>(dataBlock);
	uint32 length = encryptedBlock.length();
	stringstream decryptedBlockStream;
	stringstream encryptedBlockStream(encryptedBlock);

	prev[0] = prev[1] = 0;

	// Note encryptedBlock is in hex
	for (uint32 i = 0; i < length; i += 16)
	{
		newPrev[0] = dataBlock[0] = popHexInt(encryptedBlockStream);
		newPrev[1] = dataBlock[1] = popHexInt(encryptedBlockStream);

		decryptTeaRound(dataBlock, key);

		dataBlock[0] ^= prev[0];
		dataBlock[1] ^= prev[1];

		for (uint32 j = 0; j < 8; j++)
		{
			// p is a pointer to the dataBlock array. Simpler this way.
			decryptedBlockStream << p[j];
		}

		prev[0] = newPrev[0];
		prev[1] = newPrev[1];
	}

	return decryptedBlockStream.str();
}

void LoginEncryption::decryptTeaRound(uint32* block, const uint32* key)
{
	uint32 n = 32;
	uint32 sum = 0xc6ef3720;
	uint32 delta = 0x9e3779b9;

	while (n-- > 0)
	{
		block[1] -= ((block[0] << 4) + key[2]) ^ (block[0] + sum) ^ ((block[0] >> 5) + key[3]);
		block[0] -= ((block[1] << 4) + key[0]) ^ (block[1] + sum) ^ ((block[1] >> 5) + key[1]);
		sum -= delta;
	}
}