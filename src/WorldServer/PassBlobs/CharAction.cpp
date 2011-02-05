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
*/

#include "../WorldServer.h"

void PassBlob::CharAction(Packet* packet, GlobalTable* GTable) //GameClient* client)
{
	GameClient* client = GTable->client;
	uint32 length = packet->data->bufferLength -4;
	switch(length)
	{
		case PassBlob::ACTION_SpeakWithNPC:
			{
				Log.Debug("Receive GA_PassBlob - CHAR_ACTION - ACTION_SpeakWithNPC\n");
				try
				{
					uint32 data = packet->data->read<uint32>();
					uint32 nClientInst = packet->data->read<uint32>();
					uint16 unk1 = packet->data->read<uint16>();
					uint32 unk2 = packet->data->read<uint32>();
					uint32 unk3 = packet->data->read<uint32>();
					uint32 unk4 = packet->data->read<uint32>();
					uint32 unk5 = packet->data->read<uint32>();
					uint32 unk6 = packet->data->read<uint32>();
					uint32 unk7 = packet->data->read<uint32>();
					uint32 unk8 = packet->data->read<uint32>();
					uint32 unk9 = packet->data->read<uint32>();
					uint32 unk10 = packet->data->read<uint32>();
					uint32 unk11 = packet->data->read<uint32>();
					uint32 unk12 = packet->data->read<uint32>();
					uint32 npcId = packet->data->read<uint32>();
					uint32 unk13 = packet->data->read<uint32>();
					uint32 unk14 = packet->data->read<uint32>();
					uint32 unk15 = packet->data->read<uint32>();
					uint32 end = packet->data->read<uint32>();

					PacketBuffer aBuffer(500);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					aBuffer.write<uint32>(length);
					aBuffer.write<uint32>(CHAR_ACTION);
					aBuffer.write<uint32>(data);
					aBuffer.write<uint32>(client->nClientInst);
					aBuffer.write<uint16>(unk1 + 1);
					aBuffer.write<uint32>(unk2);
					aBuffer.write<uint32>(unk3);
					aBuffer.write<uint32>(unk4);
					aBuffer.write<uint32>(unk5);
					aBuffer.write<uint32>(unk6);
					aBuffer.write<uint32>(unk7);
					aBuffer.write<uint32>(unk8);
					aBuffer.write<uint32>(unk9);
					aBuffer.write<uint32>(unk10);
					aBuffer.write<uint32>(unk11);
					aBuffer.write<uint32>(unk12);
					aBuffer.write<uint32>(npcId);
					aBuffer.write<uint32>(unk13);
					aBuffer.write<uint32>(unk14);
					aBuffer.write<uint32>(unk15);
					aBuffer.write<uint32>(end);
					aBuffer.doItAll(client->clientSocket);
				}
				catch(char* errMsg)
				{
					Log.Error("Error at receiving ActionDatas @ ACTION_SpeakWithNPC - CharAction.cpp\nError Message:\n%s\n", errMsg);
				}
				break;
			}

		case PassBlob::ACTION_SpellAtt:
			{
				try
				{
					Log.Debug("Receive GA_PassBlob - CHAR_ACTION - ACTION_SpellAtt\n");
					uint32 unk0 = packet->data->read<uint32>();
					uint32 nClientInst = packet->data->read<uint32>();
					uint16 unk1 = packet->data->read<uint16>();
					WorldServer::HitWithSpell(GTable, packet);
				}
				catch(char* errMsg)
				{
					Log.Error("Error at receiving ActionDatas @ ACTION_SpellAtt - CharAction.cpp\nError Message:\n%s\n", errMsg);
				}
				break;
			}

		case PassBlob::ACTION_NormalAtt:
			{
				try
				{
					Log.Debug("Receive GA_PassBlob - CHAR_ACTION - ACTION_NormalAtt\n");
					uint32 unk0 = packet->data->read<uint32>();
					uint32 nClientInst = packet->data->read<uint32>();
					uint16 unk1 = packet->data->read<uint16>();
					WorldServer::HitEnemy(GTable, packet);
				}
				catch(char* errMsg)
				{
					Log.Error("Error at receiving ActionDatas @ ACTION_NormalAtt - CharAction.cpp\nError Message:\n%s\n", errMsg);
				}
				break;
			}

		case PassBlob::ACTION_WearUnwearItem:
			{
				try
				{
					Log.Debug("Receive GA_PassBlob - CHAR_ACTION - ACTION_WearUnwearItem\n");
					uint32 unk0 = packet->data->read<uint32>();
					uint32 nClientInst = packet->data->read<uint32>();
					uint16 unk1 = packet->data->read<uint16>();
					uint32 const1 = packet->data->read<uint32>();
					uint32 ItemOpcode = packet->data->read<uint32>();

					if(ItemOpcode)
					{
						uint8 *buff1 = packet->data->readArray(52);
						uint32 position = packet->data->read<uint32>();
						uint32 ItemID = packet->data->read<uint32>();
						packet->data->offset -=68;
						client->charInfo.WearItem(client,ItemID,position);
					}

					uint32 unk2 = packet->data->read<uint32>();
					uint32 unk3 = packet->data->read<uint32>();
					uint32 unk4 = packet->data->read<uint32>();
					uint32 unk5 = packet->data->read<uint32>();
					uint32 size = length - 30;

					PacketBuffer aBuffer(500);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					aBuffer.write<uint32>(length);
					aBuffer.write<uint32>(CHAR_ACTION);
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(client->nClientInst);
					aBuffer.write<uint16>(unk1+1);
					aBuffer.write<uint32>(unk2);
					aBuffer.write<uint32>(unk3);
					aBuffer.write<uint32>(unk4);
					aBuffer.write<uint32>(0x00000001);
					aBuffer.writeArray(packet->data->readArray(size), size);
					aBuffer.doItAll(client->clientSocket);
				}
				catch(char* errMsg)
				{
					Log.Error("Error at receiving ActionDatas @ ACTION_WearUnwearItem - CharAction.cpp\nError Message:\n%s\n", errMsg);
				}
				break;
			}
		default:
			{
				try
				{
					Log.Debug("Receive GA_PassBlob - CHAR_ACTION - Unknown length: 0x%08x\n", length);
					uint32 unk0 = packet->data->read<uint32>();
					uint32 nClientInst = packet->data->read<uint32>();
					uint16 unk1 = packet->data->read<uint16>();
					uint32 unk2 = packet->data->read<uint32>();
					uint32 unk3 = packet->data->read<uint32>();
					uint32 unk4 = packet->data->read<uint32>();
					uint32 unk5 = packet->data->read<uint32>();
					uint32 size = length - 30;

					PacketBuffer aBuffer(500);
					aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
					aBuffer.write<uint32>(length);
					aBuffer.write<uint32>(CHAR_ACTION);
					aBuffer.write<uint32>(0x0000c350);
					aBuffer.write<uint32>(client->nClientInst);
					aBuffer.write<uint16>(unk1+1);
					aBuffer.write<uint32>(unk2);
					aBuffer.write<uint32>(unk3);
					aBuffer.write<uint32>(unk4);
					aBuffer.write<uint32>(0x00000001);
					aBuffer.writeArray(packet->data->readArray(size), size);
					aBuffer.doItAll(client->clientSocket);
				}
				catch(char* errMsg)
				{
					Log.Error("Error at receiving ActionDatas @ ACTION_WearUnwearItem - CharAction.cpp\nError Message:\n%s\n", errMsg);
				}
				break;
			}
	}
}

/*
Temp Data

Speak with Casilda

PacketBuffer aBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000020);
								aBuffer.write<uint32>(0x96c46740);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint16>(0x0000);
								aBuffer.write<uint32>(client->charInfo.position.x);
								aBuffer.write<uint32>(client->charInfo.position.y);
								aBuffer.write<uint32>(client->charInfo.position.z);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								uint8 data1[] = { 
									0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x17, 0x2d, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e, 0x4f, 0x4f, 0x3c
								};
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000005e);
								//aBuffer.write<uint32>(0x0000001c);
								aBuffer.write<uint32>(0x045b0b0f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(data1, sizeof(data1));
								
								//aBuffer.write<uint16>(0);
								//aBuffer.write<uint8>(1);
								//aBuffer.write<uint32>(0x0000c350);
								//aBuffer.write<uint32>(npcId);
								//aBuffer.write<uint8>(1);
								
								aBuffer.doItAll(client->clientSocket);
								/*
								//Testing
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x14);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint32>(0x05010119);
								aBuffer.write<uint16>(0x0500);
								aBuffer.write<uint32>(0);
								aBuffer.doItAll(client->clientSocket);

								string send0 = "<remoteformat id=\"12493\" category=\"50002\" key=\"5Rv-_WCy8r#2cQCkd,)X\" knubot=\"529\" antitag=\"1\" ><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"7;&lt;stringentry string=&quot;&quot; flags=&quot;&quot; &gt;\" name=\"guildname\" /><parameter value=\"7;&lt;stringentry string=&quot;";
									send0 += client->charInfo.name;
									send0 += "&quot; flags=&quot;m&quot; &gt;\" name=\"name\" /><parameter value=\"7;&lt;stringentry string=&quot;Not in guild [Legacy of Crom]&quot; flags=&quot;&quot; &gt;\" name=\"npcguildname\" /><parameter value=\"7;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"7;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>";
									uint32 size = send0.size() + 2 + (7 * 4)+ (1 * 2) + (2 * 1);
								aBuffer = PacketBuffer(5000);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(size);
								aBuffer.write<uint32>(0x4a3d899a);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(1);
								aBuffer.write<string>(send0);
								aBuffer.write<uint8>(0x01);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								string send1 = "<remoteformat id=\"7615\" category=\"50001\" key=\"c(TL[h?/d2YT`!bq.&quot;Z&#92;\" knubot=\"0\"  ><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"6;&lt;stringentry string=&quot;";
									send1 += client->charInfo.name;
									send1 +="&quot; flags=&quot;fw&quot; &gt;\" name=\"name\" /><parameter value=\"6;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>";
								
								string send2 = "<remoteformat id=\"0\" category=\"50001\" key=\"v&quot;!?SRDS/&amp;(pYJlA&#92;oy2\" knubot=\"0\"  ><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"6;&lt;stringentry string=&quot;";
									send2 += client->charInfo.name;
									send2 += "&quot; flags=&quot;fw&quot; &gt;\" name=\"name\" /><parameter value=\"6;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>";
								
									size = send1.size() + send2.size() + 4 + (7 * 4) + 2 + 1;
								aBuffer = PacketBuffer(5000);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(size);
								aBuffer.write<uint32>(0xf164a4a5);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(0x00000002);
								aBuffer.write<string>(send1);
								aBuffer.write<string>(send2);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);
								//----------
								//*/ /*
								uint8 data2_0[] = { 0x00, 0x07, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0xa4, 0x18, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
								uint8 data2_1[] = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0d, 0x3e, 0x4f, 0x4f, 0x3c };
								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000001c);
								aBuffer.write<uint32>(0x045b0b0f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.writeArray(data2_0, sizeof(data2_0));
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.writeArray(data2_1, sizeof(data2_1));
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x0000001c);
								aBuffer.write<uint32>(0x045b0b0f);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint8>(0x01);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(500);
								aBuffer.writeHeader("GameCharAgent", "GameCharInterface", gameUnknown1, 0, client->nClientInst, 0, 0x14);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x05);
								aBuffer.write<uint32>(0x01011905);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint32>(0x00000000);
								aBuffer.doItAll(client->clientSocket);


								aBuffer = PacketBuffer(5000);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000593);
								aBuffer.write<uint32>(0x4a3d899a);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(0x00000001);
								aBuffer.write<string>("<remoteformat id=\"12493\" category=\"50002\" key=\"5Rv-_WCy8r#2cQCkd,)X\" knubot=\"529\" antitag=\"1\" ><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"6;&lt;stringentry string=&quot;Faolantinos&quot; flags=&quot;fw&quot; &gt;\" name=\"name\" /><parameter value=\"6;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>");
								aBuffer.write<uint8>(0x01);
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);

								aBuffer = PacketBuffer(5000);
								aBuffer.writeHeader("GameAgent", "GameInterface", gameUnknown1, 0, client->nClientInst, 0, 0x00);
								aBuffer.write<uint32>(0x00000afc);
								aBuffer.write<uint32>(0xf164a4a5);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(client->nClientInst);
								aBuffer.write<uint8>(0x00);
								aBuffer.write<uint16>(0x0001);
								aBuffer.write<uint32>(0x0000c350);
								aBuffer.write<uint32>(npcId);
								aBuffer.write<uint32>(0x00000002);
								aBuffer.write<string>("<remoteformat id=\"7615\" category=\"50001\" key=\"c(TL[h?/d2YT`!bq.&quot;Z&#92;\" knubot=\"0\"  ><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"6;&lt;stringentry string=&quot;Faolantinos&quot; flags=&quot;fw&quot; &gt;\" name=\"name\" /><parameter value=\"6;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>");
								aBuffer.write<string>("<remoteformat id=\"0\" category=\"50001\" key=\"v&quot;!?SRDS/&amp;(pYJlA&#92;oy2\" knubot=\"0\"  ><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" a\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\" t\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"a\" /><parameter value=\"6;&lt;stringentry string=&quot;Faolantinos&quot; flags=&quot;fw&quot; &gt;\" name=\"name\" /><parameter value=\"6;&lt;localized id=&quot;9411&quot; category=&quot;51000&quot; flags=&quot;f&quot; key=&quot;FcW&amp;quot;#Cq-9sA*5*@T7%Vj&quot; &gt;\" name=\"npcname\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"p\" /><parameter value=\"6;&lt;localized token=&quot;Stranger&quot; category=&quot;100&quot; flags=&quot;&quot; key=&quot;&amp;#39;TXPNx_CPtJl(C`-$1/z&quot; &gt;\" name=\"t\" /></remoteformat>");
								aBuffer.write<uint32>(0x3e4f4f3c);
								aBuffer.doItAll(client->clientSocket);
*/