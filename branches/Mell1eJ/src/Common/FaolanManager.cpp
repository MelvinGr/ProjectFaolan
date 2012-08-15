/*
Project Faolan a Simple and Free Server Emulator for Age of Conan.
Copyright (C) 2009, 2010, 2011, 2012 The Project Faolan Team

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

#include "FaolanManager.h"

// Follow UniverseAgent structure
void WriteManagerHeader(PacketBuffer &packetBuffer, FaolanManagerSenderReceivers sender, 
						FaolanManagerSenderReceivers receiver, FaolanManagerOpcodes opcode) 
{
	packetBuffer.write<uint32>(0); // Write empty length
	packetBuffer.write<uint32>(0); // write empty crc32
	packetBuffer.write<uint32>(sizeof(uint8) * 4 + sizeof(uint16)); // header length
	packetBuffer.write<uint8>(sender); // sender
	packetBuffer.write<uint8>(0); // sender length
	//packetBuffer.write(sender, sl); // sender data
	packetBuffer.write<uint8>(receiver); // receiver
	packetBuffer.write<uint8>(0); // receiver length
	//packetBuffer.write(receiver, rl); // receiver data
	packetBuffer.write<uint16>(opcode); // opcode
}