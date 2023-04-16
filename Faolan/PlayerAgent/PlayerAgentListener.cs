using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.PlayerAgent
{
	public partial class PlayerAgentListener : Server<ConanPacket>
	{
		// ReSharper disable once SuggestBaseTypeForParameter
		public PlayerAgentListener(ILogger<PlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database)
			: base(configuration.PlayerAgentPort(), logger, configuration, database)
		{
			//
		}

        // When reconnecting: Unknown packet: Opcode: 0x000020EE Length: 46 { 0x31, 0x0C, 0xEC, 0x57, 0x31, 0x0C, 0xEC, 0x57, 0x11, 0x01, 0xB4, 0xF5 }

        protected override async Task ReceivedPacket(INetworkClient client, ConanPacket packet)
		{
			Logger.LogInformation($"Received opcode: {(PlayerAgentOpcodes)packet.Opcode} ({packet.Opcode.ToHex()})");

			switch ((PlayerAgentOpcodes)packet.Opcode)
			{
				case PlayerAgentOpcodes.RequestAuth:
				{
					var accountId = packet.Data.ReadUInt32();
					var cookie = packet.Data.ReadUInt32();

					client.Account = await Database.GetAccount(accountId);
					if (client.Account == null)
						throw new Exception("client.Account == null");

					client.Account.AuthStatus = packet.Data.ReadUInt32();
					var unk1 = packet.Data.ReadUInt32();

					//if (cookie != client.Cookie)
					//Logger.Info("Cookie mismatch!");

					new PacketStream()
						.WriteHeader(Sender, Receiver, new byte[] { 0x93, 0x86, 0xee, 0x05 }, PlayerAgentResponseOpcodes.InitAuth)
						.WriteUInt32(client.Account.AuthStatus)
						.Send(client);

					break;
				}

				case PlayerAgentOpcodes.RequestSmallCharList:
				{
					var unk1 = packet.Data.ReadUInt32();
					var unk2 = packet.Data.ReadUInt16();

					new PacketStream() // packet is send for some characters
						.WriteHeader(Sender, Receiver3, new byte[] { 0xDD, 0xAD, 0x83, 0x02 }, 0x20E5)
						.WriteUInt32(0x0000C350)
						.WriteUInt32(0x1101b4f5) // char id
						.WriteArrayUInt32Length( // maybe char preview?
							0x49, 0x4F, 0x7A, 0x31, 0x22, 0x02, 0x00, 0x00, 0x78, 0xDA, 0xE3, 0x66, 0x60, 0x60, 0x28,
							0x28,
							0x4A, 0x2D, 0xCB, 0x4C, 0x2D, 0xD7, 0x4B, 0xCA, 0xCC, 0xE3, 0x67, 0x62, 0x60, 0x60, 0xE1,
							0x5E,
							0x2E, 0xC0, 0x00, 0xA4, 0x19, 0x24, 0xB3, 0xF8, 0x19, 0x9E, 0x4B, 0xF3, 0x30, 0x28, 0x32,
							0x80,
							0x40, 0x83, 0x3D, 0x03, 0x06, 0x20, 0x5D, 0x4C, 0x19, 0x88, 0xF3, 0x32, 0xF9, 0x19, 0x16,
							0xA9,
							0x30, 0x33, 0xB0, 0x52, 0x60, 0x6E, 0xFF, 0xA1, 0xD2, 0xBD, 0x20, 0xB1, 0x5F, 0xC2, 0x56,
							0x0C,
							0x6C, 0x40, 0xBE, 0x15, 0x10, 0x73, 0x01, 0xB1, 0x6A, 0x82, 0x11, 0x5C, 0x0D, 0x23, 0x10,
							0x77,
							0xBE, 0xB0, 0x84, 0xF3, 0x79, 0x80, 0xF8, 0xE5, 0x6D, 0x2E, 0x38, 0x9F, 0x1F, 0x88, 0x99,
							0x63,
							0xED, 0xC0, 0xEA, 0x40, 0xE0, 0x15, 0x33, 0x03, 0xC3, 0x8B, 0x1C, 0x76, 0x06, 0x66, 0x28,
							0xFF,
							0x3D, 0x90, 0x51, 0xEE, 0x66, 0x8C, 0xC2, 0xFF, 0x0B, 0xD4, 0x0F, 0xE3, 0x7F, 0x06, 0x32,
							0x12,
							0x1F, 0x70, 0x81, 0xC3, 0x0A, 0x04, 0x04, 0x80, 0x78, 0xAD, 0xA7, 0x0B, 0xDC, 0x7C, 0x21,
							0x20,
							0xAE, 0xD7, 0x65, 0x46, 0xF1, 0x05, 0x3B, 0x10, 0x0B, 0x02, 0xB1, 0x56, 0x49, 0x6A, 0x45,
							0x71,
							0x72, 0x51, 0x66, 0x41, 0x49, 0xBC, 0x91, 0x81, 0x91, 0xB1, 0xA5, 0x31, 0xC8, 0x33, 0x2C,
							0x58,
							0x24, 0x0D, 0x8D, 0x2D, 0x4D, 0x8C, 0x40, 0x92, 0xAC, 0x50, 0x13, 0x91, 0x24, 0x8D, 0x0D,
							0x0D,
							0x8D, 0x0D, 0x8C, 0xCC, 0x41, 0xB2, 0xA0, 0x30, 0x00, 0x69, 0x4F, 0x2C, 0x2A, 0x4E, 0xCD,
							0x17,
							0x62, 0xDE, 0xCB, 0x01, 0x64, 0x83, 0x74, 0x24, 0xE5, 0x17, 0xE7, 0xE7, 0x3A, 0x25, 0xA4,
							0xEE,
							0xE1, 0x84, 0x5A, 0x5E, 0x5C, 0x92, 0x9F, 0x9B, 0x98, 0x9C, 0x01, 0x52, 0xC3, 0x03, 0x33,
							0x31,
							0x29, 0x35, 0xB1, 0x28, 0x25, 0x37, 0xB5, 0x38, 0x03, 0x68, 0xA2, 0x85, 0xA1, 0x85, 0x85,
							0x09,
							0xC8, 0x44, 0x71, 0xA8, 0x8B, 0x01, 0x6B, 0xC9, 0x48, 0x87, 0x87)
						.Send(client);

					var characters = await Database.GetCharactersByAccount(client.Account.Id);

					var aBuffer = new PacketStream();
					aBuffer.WriteHeader(Sender, Receiver, new byte[] { 0x94, 0xa7, 0x60 }, PlayerAgentResponseOpcodes.SmallCharList);
					aBuffer.WriteUInt32((characters.Length + 1) * 1009);

					foreach (var character in characters)
					{
						aBuffer.WriteUInt32(character.Id);
						aBuffer.WriteString("ef1def800e8efe428d3c4ce3fcb4566c");
						aBuffer.WriteUInt16(0x3738); // or 0x3738, 0x3664, 0x3136, 0x3961
					}

					aBuffer.Send(client);

					break;
				}

				case PlayerAgentOpcodes.RequestCharRealmData:
				{
					var charCount = packet.Data.ReadUInt32() / 1009 - 1; // % 1009

					for (var i = 0; i < charCount; i++)
					{
						var unk1 = packet.Data.ReadUInt32();
						var unk2 = packet.Data.ReadString();

						Logger.LogDebug($"{unk1:X8}: {unk2}");
					}

					await SendCharacterList(client);
					await SendRealmList(client);

					new PacketStream()
						.WriteHeader(Sender, Receiver, new byte[] { 0xcb, 0xc6, 0xfc, 0x04 }, PlayerAgentResponseOpcodes.SendCharacterRealmData)
						.WriteUInt32(1)
						.Send(client);

					break;
				}

				case PlayerAgentOpcodes.Ox20A6:
				{
					new PacketStream() // headerdata was { 0xE0, 0xC1, 0x9C, 0x0D };
						.WriteHeader(Sender, Receiver, new byte[] { 0x99, 0x95, 0x92, 0x05 }, 0x209c)
						.WriteUInt16(0)
						.WriteByte(0)
						.Send(client);

					break;
				}

				case PlayerAgentOpcodes.EnterWorld:
				{
					var characterId = packet.Data.ReadUInt32();
					var unk0 = packet.Data.ReadUInt32();
					var unk1 = packet.Data.ReadUInt32();
					var unk2 = packet.Data.ReadUInt32();
					var unk3 = packet.Data.ReadByte();
					var unk4 = packet.Data.ReadUInt32();
					var unk5 = packet.Data.ReadUInt32();
					var unk6 = packet.Data.ReadUInt32();

					await Database.UpdateClientInstance(client.Account, characterId);
					await Database.UpdateLastInfo(client.Character, client);

					SendAgentServer(client);
					SendCsPlayerAgent(client);
					SendGameServer(client);

                    var headerData638172634263791750_0_0 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0x01, 0x20, 0xE3, 0xEE, 0x9F, 0xE2, 0x07 };
                    var sender638172634263791750_0_0 = new byte[] { 0x0D, 0x16, 0x91, 0x35, 0x1D, 0x10, 0x5A };
                    var receiver638172634263791750_0_0 = new byte[] { 0x0D, 0x38, 0x57, 0x15, 0x7D, 0x10, 0xEC, 0xEB, 0x80, 0xDE, 0x03 };
                    var packetData638172634263791750_0_0 = new byte[] { 0x00, 0x00, 0x4E, 0xD4, 0x00, 0x10, 0x41, 0x41, 0x50, 0x6F, 0x69, 0x6E, 0x74, 0x73, 0x50, 0x65, 0x72, 0x4C, 0x65, 0x76, 0x65, 0x6C, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x41, 0x6C, 0x74, 0x65, 0x72, 0x6E, 0x61, 0x74, 0x65, 0x41, 0x64, 0x76, 0x61, 0x6E, 0x63, 0x65, 0x6D, 0x65, 0x6E, 0x74, 0x5F, 0x47, 0x65, 0x6E, 0x65, 0x72, 0x69, 0x63, 0x54, 0x6F, 0x6B, 0x65, 0x6E, 0x73, 0x41, 0x77, 0x61, 0x72, 0x64, 0x65, 0x64, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x41, 0x6C, 0x74, 0x65, 0x72, 0x6E, 0x61, 0x74, 0x65, 0x41, 0x64, 0x76, 0x61, 0x6E, 0x63, 0x65, 0x6D, 0x65, 0x6E, 0x74, 0x5F, 0x53, 0x70, 0x65, 0x63, 0x69, 0x66, 0x69, 0x63, 0x54, 0x6F, 0x6B, 0x65, 0x6E, 0x73, 0x41, 0x77, 0x61, 0x72, 0x64, 0x65, 0x64, 0x40, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x41, 0x6E, 0x6E, 0x69, 0x76, 0x65, 0x72, 0x73, 0x61, 0x72, 0x79, 0x32, 0x30, 0x32, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1B, 0x41, 0x6E, 0x6E, 0x69, 0x76, 0x65, 0x72, 0x73, 0x61, 0x72, 0x79, 0x32, 0x30, 0x32, 0x30, 0x5F, 0x43, 0x61, 0x6E, 0x47, 0x65, 0x74, 0x41, 0x72, 0x6D, 0x6F, 0x72, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x19, 0x41, 0x6E, 0x6E, 0x69, 0x76, 0x65, 0x72, 0x73, 0x61, 0x72, 0x79, 0x5F, 0x35, 0x5F, 0x53, 0x70, 0x61, 0x77, 0x6E, 0x42, 0x6F, 0x73, 0x73, 0x65, 0x73, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x43, 0x75, 0x72, 0x72, 0x65, 0x6E, 0x74, 0x53, 0x65, 0x61, 0x73, 0x6F, 0x6E, 0x40, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x16, 0x44, 0x6F, 0x75, 0x62, 0x6C, 0x65, 0x52, 0x61, 0x69, 0x64, 0x46, 0x69, 0x6E, 0x64, 0x65, 0x72, 0x54, 0x6F, 0x6B, 0x65, 0x6E, 0x73, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x15, 0x45, 0x76, 0x65, 0x6E, 0x74, 0x41, 0x6E, 0x6E, 0x69, 0x76, 0x65, 0x72, 0x73, 0x61, 0x72, 0x79, 0x5F, 0x32, 0x30, 0x31, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x45, 0x76, 0x65, 0x6E, 0x74, 0x44, 0x6F, 0x75, 0x62, 0x6C, 0x65, 0x58, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0x45, 0x76, 0x65, 0x6E, 0x74, 0x48, 0x61, 0x6C, 0x6C, 0x6F, 0x77, 0x65, 0x65, 0x6E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x45, 0x78, 0x70, 0x61, 0x6E, 0x64, 0x49, 0x6E, 0x76, 0x65, 0x6E, 0x74, 0x6F, 0x72, 0x69, 0x65, 0x73, 0x5F, 0x4D, 0x61, 0x78, 0x42, 0x61, 0x6E, 0x6B, 0x53, 0x6C, 0x6F, 0x74, 0x73, 0x40, 0x72, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x49, 0x73, 0x50, 0x76, 0x50, 0x53, 0x65, 0x72, 0x76, 0x65, 0x72, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x16, 0x50, 0x76, 0x45, 0x58, 0x50, 0x46, 0x72, 0x6F, 0x6D, 0x50, 0x76, 0x50, 0x4D, 0x75, 0x6C, 0x74, 0x69, 0x70, 0x6C, 0x69, 0x65, 0x72, 0x3F, 0xB9, 0x99, 0x99, 0x99, 0x99, 0x99, 0x9A, 0x00, 0x14, 0x52, 0x61, 0x69, 0x64, 0x46, 0x69, 0x6E, 0x64, 0x65, 0x72, 0x45, 0x76, 0x65, 0x6E, 0x74, 0x43, 0x68, 0x65, 0x73, 0x74, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x57, 0x6F, 0x72, 0x6C, 0x64, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x53, 0x70, 0x61, 0x77, 0x6E, 0x44, 0x61, 0x79, 0x5F, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x31, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x19, 0x57, 0x6F, 0x72, 0x6C, 0x64, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x53, 0x70, 0x61, 0x77, 0x6E, 0x44, 0x61, 0x79, 0x5F, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1B, 0x57, 0x6F, 0x72, 0x6C, 0x64, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x53, 0x70, 0x61, 0x77, 0x6E, 0x57, 0x65, 0x65, 0x6B, 0x5F, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x31, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x57, 0x6F, 0x72, 0x6C, 0x64, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x53, 0x70, 0x61, 0x77, 0x6E, 0x57, 0x65, 0x65, 0x6B, 0x5F, 0x42, 0x6F, 0x73, 0x73, 0x5F, 0x33, 0x40, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                    new PacketStream()
                        .WriteHeader(sender638172634263791750_0_0, receiver638172634263791750_0_0, headerData638172634263791750_0_0, 0x00001A07, true)
                        .WriteArray(packetData638172634263791750_0_0)
                    .Send(client);

                    break;
				}

				case PlayerAgentOpcodes.Ox20D6:
				{
					var languageId = packet.Data.ReadUInt32();
					//Logger.Info($"Maybe report language (Language: {languageId})");

					break;
				}

				case PlayerAgentOpcodes.Ox20EC:
				{
					//

					break;
				}

				case PlayerAgentOpcodes.CreateCharacter:
				{
					var realmId = packet.Data.ReadUInt32();
					var newCharacter = await Database.CreateCharacter(client.Account.Id, realmId);
					await Database.UpdateClientInstance(client.Account, newCharacter.Id);

					new PacketStream()
						.WriteHeader(Sender4, Receiver, new byte[] { 0x8b, 0xd8, 0x99, 0x02 }, PlayerAgentResponseOpcodes.CreateCharacter)
						.WriteUInt32(0x0000c350)
						.WriteUInt32(client.Account.ClientInstance)
						.WriteUInt32(0)
						.Send(client);

					break;
				}

				case PlayerAgentOpcodes.DeleteCharacter:
				{
					var data = packet.Data.ToArray();
					Logger.LogInformation("DELETE CHAR");

					break;
				}

				default:
				{
					Logger.LogWarning($"Unknown packet: {packet}");
					break;
				}
			}
		}
	}
}
