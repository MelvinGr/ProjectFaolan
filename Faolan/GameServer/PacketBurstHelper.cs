using System;
using System.Collections.Generic;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private static readonly byte[] LatencyResponseSender = [0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x08];

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		private static readonly byte[] LoginWorld201CSender = [0x0D, 0x5D, 0xB9, 0xEC, 0xA9, 0x10, 0x58];
		// The create-flow packets are written explicitly (PacketStream.WriteHeader + WriteArray + WriteUInt32)
		// in their Retail*Burst sender methods. The client instance is the character's real id: it is written
		// into the body with WriteUInt32(id) and appended to the relevant conversation-id (sender/receiver) as
		// a protobuf varint via AppendVarint.

		// The retail login world-entry rides ONE conversation for everything (auth, MapChange, world objects,
		// player and NPC spawns, time): family prefix + sub-id 0x58 on the sender, and the receiver family
		// prefix + the character instance as a varint. Confirmed from the Strie login capture. Keeping every
		// login packet on this single conversation is what lets the client tie the spawns to the loaded
		// playfield and render them (the player avatar renders regardless, but NPCs do not).
		private static readonly byte[] LoginWorldSender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58];

		// Ping/latency replies ride a different conversation SUB-ID (0x08) than the world burst (0x58) - confirmed
		// from gameplay dumps (Pong 0x2009 and LatencyCheckResponse 0x2082 both consistently use 0x08). Same
		// endpoint TYPEs as the world conversation: Pong from GameAgent (0x13CE71B1), latency from GameCharAgent
		// (0xA9ECB95D).
		private static readonly byte[] PingResponseSender = [0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x08];

		// Appends a uint32, protobuf-varint encoded, to a conversation-id prefix (the receiver/sender family),
		// producing the full conversation id that addresses the given client instance.
		private static byte[] AppendVarint(byte[] prefix, uint value)
		{
			var varint = EncodeVarint(value);
			var result = new byte[prefix.Length + varint.Length];
			Array.Copy(prefix, result, prefix.Length);
			Array.Copy(varint, 0, result, prefix.Length, varint.Length);
			return result;
		}

		private static byte[] EncodeVarint(uint value)
		{
			var bytes = new List<byte>(5);
			do
			{
				var b = (byte)(value & 0x7F);
				value >>= 7;
				if (value != 0)
					b |= 0x80;
				bytes.Add(b);
			}
			while (value != 0);

			return bytes.ToArray();
		}

		private static byte[] LoginWorld201CReceiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GameSpawnEndpointType, client.Session.Account.ClientInstance);
		}

		private static byte[] LoginWorldReceiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GamePlayerType, client.Session.Account.ClientInstance);
		}
	}
}
