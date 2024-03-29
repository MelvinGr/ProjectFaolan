using Faolan.Core.Enums;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private void HandleCommandPacket(INetworkClient client, ConanPacket packet)
		{
			var recObjTyp = packet.Data.ReadUInt32();
			var recClientInst = packet.Data.ReadUInt32();
			var unk0 = packet.Data.ReadByte();
			var recDataLength = packet.Data.ReadUInt32();
			var unk2 = packet.Data.ReadUInt32();
			var unk3 = packet.Data.ReadByte();
			var runByte = packet.Data.ReadByte();

			uint recVal = 0;
			var runId = 0;
			while (runByte != 0x22)
			{
				switch (runId)
				{
					case 0:
					{
						recVal = runByte;
						break;
					}

					case 1:
					{
						recVal += runByte * 0x100u;
						break;
					}

					case 2:
					{
						recVal += runByte * 0x10000u;
						break;
					}

					case 3:
					{
						recVal += runByte * 0x1000000u;
						break;
					}
				}

				runByte = packet.Data.ReadByte();
				runId++;
			}

			var recCmd = packet.Data.ReadStringByteLength();
			var strEnd = packet.Data.ReadByte();
			var unk4 = packet.Data.ReadUInt16(); // MELVIN
			var recSecCmd = packet.Data.ReadStringByteLength();

			switch (recCmd)
			{
				case "IntroDone":
				{
					var aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(0x00000019);
					aBuffer.WriteUInt32(0x96b8dc59);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteByte(0);
					aBuffer.WriteUInt32(0x00000001);
					aBuffer.WriteUInt32(0x000000ba);
					aBuffer.WriteUInt32(0);
					aBuffer.Send(client);
					break;
				}

				case "ChangeSex":
				{
					client.Character.Sex = (Sex)recVal;
					break;
				}

				case "ChangeRace":
				{
					client.Character.Race = (Race)recVal;
					break;
				}

				case "ChangeHeadMesh":
				{
					client.Character.Body.HeadMesh = recVal;
					break;
				}

				case "ChangeSize":
				{
					client.Character.Body.Size = (byte)recVal;
					break;
				}

				case "ChangeClass":
				{
					client.Character.Class = (Class)recVal;
					break;
				}

				case "ChangeVoice":
				{
					client.Character.Body.Voice = (byte)recVal;
					break;
				}

				case "SetMorphValue":
				{
					Logger.LogInformation($"Second Command: {recSecCmd} with value: {recVal}");
					if (recSecCmd.Length > 0)
					{
						//vector<string> scriptData = String::splitString(recSecCmd, "_");
						//Logger.Info("Splitted String: %s - " + scriptData[0], scriptData[1]);
						client.Account.CreateCounter++;
						client.Account.CreateState = 1;

						new PacketStream().WriteHeader(Sender2, Receiver2, null, 0x2000)
							.WriteArrayUInt32Length(new ConanStream()
								//aBuffer.WriteUInt32(recSecCmd.Length + (5*4) + (1*2) + (2*1)); // length
								.WriteUInt32(0xbadf5a4b)
								.WriteUInt32(0x0000c350)
								.WriteUInt32(client.Account.ClientInstance)
								.WriteByte(0)
								.WriteString(recSecCmd)
								.WriteUInt32(0x3f800000)
								.WriteByte(0)
								.WriteUInt32(client.Account.CreateCounter + 1))
							.Send(client);
					}
					else
						client.Account.CreateCounter = 0;

					break;
				}

				case "TheNameIs":
				{
					Logger.LogInformation($"Attempt to create Char with the name: {recSecCmd}");
					client.Character.Name = recSecCmd;

					//MELVIN 
					/*if (!Database.isValidCharName(client.client.Account.charInfo.Name))
					{
					    //TODO add missing return packet
					    Logger.Info("Charname is incorrect !");
					    closesocket(client.client.Account.clientSocket);
					    break;
					*/

					var aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(client.Character.Name.Length + 5 * 4 + 1 * 2 + 1 * 1);
					aBuffer.WriteUInt32(0xadce0cda);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteUInt32(0);
					aBuffer.WriteUInt32(0x03010000);
					aBuffer.WriteByte(0);
					aBuffer.WriteString(client.Character.Name);
					aBuffer.Send(client);

					var data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x1f, 0x08, 0x05, 0x10, 0x02, 0x18, 0x00, 0x22 };
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(client.Character.Name.Length + 10 + 1 + data.Length + 4 * 4 + 1 * 2 + 2 * 1);
					aBuffer.WriteUInt32(0xa36d3b74);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteArray(data);
					aBuffer.WriteString("NicknameOk");
					aBuffer.WriteByte(0x2a);
					aBuffer.WriteString(client.Character.Name);
					aBuffer.WriteUInt32(0x32040800);
					aBuffer.WriteUInt16(0x1000);
					aBuffer.Send(client);

					var data2 = new byte[]
					{
						0x00, 0x00, 0x00, 0x07, 0xe2, 0x00, 0x00, 0xcf, 0xef, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
						0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
					};
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(data2.Length + 3 * 4);
					aBuffer.WriteUInt32(0xf508f4c1);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteArray(data2);
					aBuffer.Send(client);

					var data3 = new byte[]
					{
						0x00, 0x00, 0x00, 0x00, 0x24, 0x08, 0x05, 0x10, 0x02, 0x18, 0x00, 0x22, 0x14, 0x43, 0x68, 0x61, 0x72, 0x43, 0x72, 0x65, 0x61, 0x74, 0x69, 0x6f, 0x6e, 0x46, 0x69, 0x6e, 0x69, 0x73, 0x68, 0x65, 0x64,
						0x2a, 0x00, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00
					};
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(data3.Length + 3 * 4);
					aBuffer.WriteUInt32(0xa36d3b74);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteArray(data3);
					aBuffer.Send(client);

					var data4 = new byte[] { 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf6, 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x0a, 0x08, 0xc5, 0xc3, 0x02, 0x18, 0x01, 0x28, 0x18, 0x30, 0x00 };
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(data4.Length + 1 * 4);
					aBuffer.WriteUInt32(0x642cd3d6);
					aBuffer.WriteArray(data4);
					aBuffer.Send(client);

					var data5 = new byte[] { 0x00, 0x00, 0xc3, 0x50, 0x00, 0x00, 0x27, 0xf7, 0x00, 0x04, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x0a, 0x08, 0xc5, 0xc3, 0x02, 0x18, 0x01, 0x28, 0x18, 0x30, 0x00 };
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(data5.Length + 1 * 4);
					aBuffer.WriteUInt32(0x642cd3d6);
					aBuffer.WriteArray(data5);
					aBuffer.Send(client);

					var data6 = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x6a, 0x00, 0x00, 0x00, 0x14 };
					aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender2, Receiver2, null, 0x2000);
					aBuffer.WriteUInt32(data6.Length + 3 * 4);
					aBuffer.WriteUInt32(0xf98e10b3);
					aBuffer.WriteUInt32(0x0000c350);
					aBuffer.WriteUInt32(client.Account.ClientInstance);
					aBuffer.WriteArray(data6);
					aBuffer.Send(client);

					break;
				}

				default:
				{
					Logger.LogInformation("Receive unknown command: {0} with value {1}", recCmd, recVal);
					break;
				}
			}
		}
	}
}
