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

#include "Client.h"

void CharacterInfo::Update(GameClient* client)
{
	if(client->charInfo.hp < 0)
		client->charInfo.hp = 0;

	if(client->charInfo.stamina < 0)
		client->charInfo.stamina = 0;

	if(client->charInfo.mana < 0)
		client->charInfo.mana = 0;

	if(client->charInfo.hp == 0)
	{
		Log.Notice("Character %s is dead!\n", client->charInfo.name.c_str());
	}

	if(true) //client->charInfo.hp > 0 && !client->charInfo.combat.inCombat)
	{
		if(state != SPRINTING)
			RegenerateStamina(client);

		RegenerateMana(client);
		RegenerateHealth(client);
	}

	PacketBuffer aBuffer(500);

	switch(state)
	{
	case SPRINTING:
		{
			//19,84126984126984%
			if(client->charInfo.stamina > 0)
			{
				if((client->charInfo.stamina - (client->charInfo.curMaxStats.stamina*0.19)) >= 0)
				{
					client->charInfo.stamina -= (client->charInfo.curMaxStats.stamina*0.19);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					aBuffer.write<uint32>(0x0000001d);
					aBuffer.write<uint32>(0x96b8dc59);
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(client->nClientInst);
					aBuffer.write<uint8>(0x00);
					aBuffer.write<uint32>(0x00000001);
					aBuffer.write<uint32>(0x000001f8);
					aBuffer.write<uint32>(client->charInfo.stamina*100);
					aBuffer.write<uint32>(0x3e4f4f3c);
					aBuffer.doItAll(client->clientSocket);
					//Log.Notice("Stamina[is_spr]:%i\n",client->charInfo.stamina);

				}
				else
				{
					client->charInfo.stamina = 0;
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					aBuffer.write<uint32>(0x0000001d);
					aBuffer.write<uint32>(0x96b8dc59);
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(client->nClientInst);
					aBuffer.write<uint8>(0x00);
					aBuffer.write<uint32>(0x00000001);
					aBuffer.write<uint32>(0x000001f8);
					aBuffer.write<uint32>(0x00000000);//set to 0 
					aBuffer.write<uint32>(0x3e4f4f3c);
					aBuffer.doItAll(client->clientSocket);	
					//Log.Notice("Stamina[is_spr]:%i\n",client->charInfo.stamina);
				}

				break;
			}
		}
	}


	if(client->charInfo.stamina <= 10)
		client->charInfo.ApplyAura(client, 2960);
	else if (client->charInfo.isExhausted)
		client->charInfo.RemoveAura(client, 2960);
}

void CharacterInfo::RegenerateStamina(GameClient* client)
{	
	int regen = 20;

	if(client->charInfo.stamina + regen < client->charInfo.curMaxStats.stamina)
		client->charInfo.stamina += regen;
	else client->charInfo.stamina = client->charInfo.curMaxStats.stamina;

	/*aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x28d0ac99); // modify energy I guess
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000000);// here updates hp for now only stamina will be updated
	aBuffer.write<uint32>(0x00000000);
	aBuffer.write<uint32>(2000);//424(23) E20(48) here before energy = 12.00 +424(10.60) = 23.xx
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);*/

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x000001f8);
	aBuffer.write<uint32>(client->charInfo.stamina*100);//set to 0 
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	//Log.Notice("Stamina[regen]:%u\n",client->charInfo.stamina);
}

void CharacterInfo::RegenerateMana(GameClient* client)
{
	int regen = 20;

	if(client->charInfo.mana + regen < client->charInfo.curMaxStats.mana)
		client->charInfo.mana += regen;
	else client->charInfo.mana = client->charInfo.curMaxStats.mana;

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x000001fa);
	aBuffer.write<uint32>(client->charInfo.mana*100);//set to 0 
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);
	//Log.Notice("Stamina[regen]:%u\n",client->charInfo.stamina);
}

void CharacterInfo::RegenerateHealth(GameClient* client)
{		
	int regen = 10;
	if(client->charInfo.hp <=0)
	{   // #define GameCharAgent::ResurrectMe 0x1F
		// #define GameCharInterface::ResurrectRequest 0x15
		PacketBuffer aBuffer(500);	
		
		//Set Dead stance
		uint8 pack2[] = {0x00, 0x04, 0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0xa3, 0x95, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x49, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xdc, 0x3e, 0x4f, 0x4f, 0x3c};
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000004a);
		aBuffer.write<uint32>(0x642cd3d6);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.writeArray(pack2,sizeof(pack2));
		aBuffer.doItAll(client->clientSocket);
		
		//Resurrect Stuff
		//this open load screen
		/*uint8 dead[] = {0x01, 0x62, 0x00, 0x00, 0xc7, 0x9c, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x00, 0x00, 0x01, 0x09, 0xe2, 0x7d, 0x00, 0x00, 0x00, 0x0b, 0x00, 0x00, 0x9c, 0x50, 0x00, 0x79, 0xd4, 0x64, 0x01, 0x3e, 0x4f, 0x4f, 0x3c};
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000002f);
		aBuffer.write<uint32>(0x10d27bc0);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.writeArray(dead,sizeof(dead));
		aBuffer.doItAll(client->clientSocket);
		
		
		//teleport to ressurection point (LATER TODO: find where is the map id or x y z
		uint8 pack1_0[] = {0x00, 0x44, 0x14, 0x4d, 0x71, 0x43, 0x31, 0x4b, 0x14, 0x44, 0x33, 0xb0, 0xa4, 0x00, 0x00, 0x00, 0x00, 0x3f, 0x67, 0x17, 0x3c, 0x00, 0x00, 0x00, 0x00, 0x3e, 0xdc, 0x4c, 0xe5, 0x62, 0x00, 0x00, 0xc7, 0xa0, 0x00, 0x00, 0x0f, 0xaa, 0x00, 0x00, 0x00, 0x00, 0x01, 0x09, 0xe2, 0x7d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x86, 0xaa, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x3e, 0x4f, 0x4f, 0x3c};
		aBuffer = PacketBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
		aBuffer.write<uint32>(0x0000005f);
		aBuffer.write<uint32>(0x5a32f0d7);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.writeArray(pack1_0,sizeof(pack1_0));
		aBuffer.doItAll(client->clientSocket);*/

		Log.Notice("Player:%s is dead\n\n", client->charInfo.name.c_str());
	
	}
	if(client->charInfo.hp + regen < client->charInfo.curMaxStats.health)
		client->charInfo.hp += regen;
	else client->charInfo.hp = client->charInfo.curMaxStats.health;

	PacketBuffer aBuffer(500);
	aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
	aBuffer.write<uint32>(0x0000001d);
	aBuffer.write<uint32>(0x96b8dc59);
	aBuffer.write<uint32>(0x0000c350);
	aBuffer.write<uint32>(client->nClientInst);
	aBuffer.write<uint8>(0x00);
	aBuffer.write<uint32>(0x00000001);
	aBuffer.write<uint32>(0x0000001b);
	aBuffer.write<uint32>((client->charInfo.hp)*100);//b3ce= 46030
	aBuffer.write<uint32>(0x3e4f4f3c);
	aBuffer.doItAll(client->clientSocket);

	//Log.Notice("CURR HPREGEN:%i\n\n", client->charInfo.hp);
}

void CharacterInfo::InitalizeStatupStats(GameClient* client)
{
	Log.Notice("Class that you have logged on is %u\n",Class);
	switch(Class)
	{
	default: //all classes have the same Attributes at start
		{
			curMaxStats = maxStats; //we need the orginal one and for modifiers too
			PlayerAttributes.CalcAttrb(client); //Update Health stuff
			hp = maxStats.health; //no mod with attributes 
			mana = maxStats.mana; //Barb doesnt have mana so it's not required 
			stamina = maxStats.stamina;
			curExp = 0;
			isExhausted = false;
			break;
		}
	}
}

void CharacterInfo::ApplyAura(GameClient *client, uint32 id)
{
	if(id == 2960 && !client->charInfo.isExhausted) //exhausted
	{
		//after 18% of health it  disappear
		uint8 a1_0[] =
		{ 
			0x00, 
			0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 0x27, 0x10, //10000 
			0x00, 0x01, 
			0x00, 0x00, 0x00, 0x01, 
			0x00, 0x00, 0x00, 0x00
		};						
		uint8 a1_1[] = 
		{
			0x00, 0x00, 
			0x00, 0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x00, 0x00, 
			0x00, 0x01, 
			0x00, 0x00, 0x00, 0x00, 
			0x20, 0x66, 0x31, 0x90, 
			0x3e, 0x4f, 0x4f, 0x3c 
		};

		PacketBuffer aBuffer(500);
		aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
		aBuffer.write<uint32>(0x0000004c);
		aBuffer.write<uint32>(0x6b87e316);
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.write<uint8>(0x00);
		aBuffer.write<uint32>(id); //id
		aBuffer.write<uint32>(0x0000c350);
		aBuffer.write<uint32>(client->nClientInst);
		aBuffer.writeArray(a1_0, sizeof(a1_0));
		aBuffer.write<uint32>(id);
		aBuffer.writeArray(a1_1, sizeof(a1_1));
		aBuffer.doItAll(client->clientSocket);

		client->charInfo.isExhausted = true;
	}
}

void CharacterInfo::RemoveAura(GameClient *client, uint32 id)
{
	PacketBuffer aBuffer(500);

	switch (id)
	{
	case 2960:
		{
			int32 HealthPeak = client->charInfo.curMaxStats.health * 0.18;
			if(client->charInfo.hp >= HealthPeak && client->charInfo.isExhausted)
			{
				aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00); // PassBlob
				aBuffer.write<uint32>(0x00000026);
				aBuffer.write<uint32>(0x6b87e316);
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint8>(0x00);
				aBuffer.write<uint32>(id);
				aBuffer.write<uint32>(0x0000c350);
				aBuffer.write<uint32>(client->nClientInst);
				aBuffer.write<uint8>(0x01);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x00000000);
				aBuffer.write<uint32>(0x3e4f4f3c);
				aBuffer.doItAll(client->clientSocket);
				client->charInfo.isExhausted = false;
			}

			break;
		}
	}
}

void CharacterInfo::WearItem(GameClient* client, uint32 id, uint32 pos)
{
	// A lot to do here because of items Attributes ;/
	//this is now only for testing 
	Log.Notice("You worn item %u : pos :%u\n",id,pos);
	//GetDetail INfo about item from item_details for eg strength etc 
	// for now I've just put by hard values from Necklace of Tireless Sagacity (80) 
	//this item adds 25 points of Constitution
	if(id == 3778665)
	{
		client->charInfo.PlayerAttributes.Constitution += 25;
		client->charInfo.PlayerAttributes.CalcAttrb(client);
	}

	switch(pos)
	{
	case ID_MAINWEAPON_LEFT:
		{
			client->charInfo.Outfit.ID_MAINWEAPON_LEFT = id;
			break;
		}
	}

	client->charInfo.PlayerAttributes.CalcAttrb(client);
}

void CharacterInfo::Attributes::CalcAttrb(GameClient* client)
{
	uint32 hpbuffer = 0 ;
	uint32 manabuffer = 0 ;
	uint32 staminabuffer = 0 ;
	//here probably switch to classes because class has other dependences like Constitution for warrios is 8 for mags  6 or 5  
	//Constitution
	hpbuffer += client->charInfo.PlayerAttributes.Constitution*8;  //FIXMe depends on class
	staminabuffer += client->charInfo.PlayerAttributes.Constitution*2;
	//Strength
	staminabuffer+=client->charInfo.PlayerAttributes.Strength*2; //it mod
	//Inteligence
	manabuffer += client->charInfo.PlayerAttributes.Intelligence*3;
	//Dexterity
	staminabuffer += client->charInfo.PlayerAttributes.Dexterity*2;
	//Wisdom
	manabuffer += client->charInfo.PlayerAttributes.Wisdom*3;

	//Log.Notice("HP : %u Stamina %u\nConstitution points: %u",hpbuffer,staminabuffer,client->charInfo.PlayerAttributes.Constitution);
	if(client->charInfo.curMaxStats.mana==0)
	{
		client->charInfo.curMaxStats.stamina  = client->charInfo.maxStats.stamina +  staminabuffer;
		client->charInfo.curMaxStats.health = client->charInfo.maxStats.health +  hpbuffer;
	}
	else
	{
		client->charInfo.curMaxStats.stamina  = client->charInfo.maxStats.stamina +  staminabuffer;
		client->charInfo.curMaxStats.health = client->charInfo.maxStats.health +  hpbuffer;
		client->charInfo.curMaxStats.mana = client->charInfo.maxStats.mana + manabuffer;
	}
}

//////////////////////////////////////////////////////////////////////////////////////////////////

GameClient::GameClient(SOCKET socket)
{
	try
	{
		clientSocket = socket;
		ipAddress = Network::GetIPFromSocket(clientSocket);
		receiveStream = 0;
		sendStream = 0;

		#ifdef WINDOWS
			threadID = GetCurrentThreadId();
		#else
			threadID = pthread_self();
		#endif

		initializedBuffer = false;
		isConnected = true;

		authChallenge = "32423423592352";

		nClientInst = 0;
		nCookie = 0;
		counter = 0;

		m_o1c = new uint32[5];
		m_data1c = new uint32[5];

		loadedChar = new vector<uint32>();
		spawnedMobs = new vector<uint32>();
	}
	catch(char* msg)
	{
		Log.Error("Crash: Catched Crash at function GameClient(SOCKET socket) @ Client.cpp\nErrorMsg: %s \n", msg);
	}
}

GameClient::~GameClient()
{
	closesocket(clientSocket);

	if(receiveStream != 0)
	{
		inflateEnd(receiveStream);
		delete receiveStream;
	}

	if(sendStream != 0)
	{
		deflateEnd(sendStream);
		delete sendStream;
	}

	delete[] m_o1c;
	delete[] m_data1c;

	delete loadedChar;
	delete spawnedMobs;
}

bool GameClient::Send(PacketBuffer* buffer, bool compressed)
{
	if (buffer->bufferLength == 0)
		return true;

	uint8 output[32768];

	sendStream->avail_in = buffer->bufferLength;
	sendStream->next_in = buffer->buffer;

	do
	{
		sendStream->avail_out = sizeof(output);
		sendStream->next_out = output;

		if (int ret = deflate(sendStream, Z_SYNC_FLUSH) < 0)
		{
			Log.Error("zlib error during compression: %i\n", ret);
			return false;
		}

		if (!send(clientSocket, (int8*)output, sizeof(output) - sendStream->avail_out, 0))
		{
			Log.Error("send() Error!\n");
			return false;
		}
	} while (sendStream->avail_out == 0); 

	return true;
}

bool GameClient::Receive(bool compressed)
{
	try
	{
		int8 receiveBuf[65536];
		int32 receiveSize = recv(clientSocket, receiveBuf, sizeof(receiveBuf), 0);
		if(receiveSize == -1 || receiveSize == 0)
		{
			Log.Notice("Client Disconnected!\n");
			isConnected = false;
			return false;
		}

		if(compressed)
		{
			uint8 zlibBuffer[65792];
			memset(&zlibBuffer, 0, sizeof(zlibBuffer));

			bool first = (receiveStream == 0);

			if(first)
				receiveStream = new z_stream();

			receiveStream->next_out = (Bytef*)zlibBuffer;
			receiveStream->avail_out = sizeof(zlibBuffer);
			receiveStream->zalloc = 0;
			receiveStream->zfree = 0;

			if(first)
			{
				receiveStream->next_in = (Bytef*)receiveBuf + 9;
				receiveStream->avail_in = receiveSize - 9;
				inflateInit(receiveStream);
			}
			else
			{
				receiveStream->next_in = (Bytef*)receiveBuf;
				receiveStream->avail_in = receiveSize;
			}

			if(int32 error = inflate(receiveStream, Z_SYNC_FLUSH) != 0)
			{
				Log.Error("Error while decompressing packet! (Zlib Error: %i)\n", error);
				// delete[] zlibBuffer
				return false;
			}

			receiveBuffer = PacketBuffer(zlibBuffer, sizeof(zlibBuffer) - receiveStream->avail_out);
		}
		else
			receiveBuffer = PacketBuffer((uint8*)receiveBuf, receiveSize);

		return true;
	}
	catch(char* msg)
	{
		Log.Error("Crash: Catched Crash at function GameClientReceive(bool compressed) @ Client.cpp\nErrorMsg: %s \n", msg);
		return false;
	}
}

Packet* GameClient::getNextPacket(bool compressed)
{
	if(!initializedBuffer)
	{
		if(!Receive(compressed))
			return 0;

		initializedBuffer = true;
	}

	if(receiveBuffer.offset < receiveBuffer.bufferLength)
	{
		Packet* packet = new Packet(&receiveBuffer);
		if(!CheckPacketCRC(packet)) // Request new packet
		{
			Log.Warning("Got incorrect Packet (wrong CRC)!\n\n");
			Log.Notice("[%s][%s] Received Opcode: 0x%08X (%s)\n", String::timeString().c_str(),
			ipAddress.c_str(), packet->opcode, packet->receiver.c_str());
			Log.Notice("%s\n\n", String::arrayToHexString(packet->data->buffer, packet->data->bufferLength).c_str());
			//packet.drop();
			//continue;
		}
		/*
		Log.Notice("[%s][%s] Received Opcode: 0x%08X (%s)\n", String::timeString().c_str(),
			ipAddress.c_str(), packet->opcode, packet->receiver.c_str());
		//*/

		return packet;
	}
	else 
	{
		receiveBuffer.drop();
		initializedBuffer = false;
	}

	return 0;
}