using System;
using System.Collections.Generic;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private static readonly byte[] LatencyResponseSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameSpawnCallerType, 0x08);

		// The 0x201C handshake uses a different conversation family, but the same sub-id 0x58 + instance tail.
		private static readonly byte[] LoginWorld201CSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameSpawnCallerType, 0x58);
		// The create-flow packets are written explicitly (PacketStream.WriteHeader + WriteArray + WriteUInt32) in
		// their Retail*Burst sender methods. The client instance is the character's real id: it is written into the
		// body with WriteUInt32(id) and woven into the sender/receiver conversation id by building each endpoint
		// through ConanEndpoints.EndpointBytes(type, id).

		// The retail login world-entry rides ONE conversation for everything (auth, MapChange, world objects,
		// player and NPC spawns, time): family prefix + sub-id 0x58 on the sender, and the receiver family
		// prefix + the character instance as a varint. Confirmed from the Strie login capture. Keeping every
		// login packet on this single conversation is what lets the client tie the spawns to the loaded
		// playfield and render them (the player avatar renders regardless, but NPCs do not). The bytes live on
		// World.WorldFraming (the single source shared with the WorldBroadcaster); these delegate to it.
		private static readonly byte[] LoginWorldSender = WorldFraming.Sender;

		// Ping/latency replies ride a different conversation SUB-ID (0x08) than the world burst (0x58) - confirmed
		// from gameplay dumps (Pong 0x2009 and LatencyCheckResponse 0x2082 both consistently use 0x08). Same
		// endpoint TYPEs as the world conversation: Pong from GameAgent (0x13CE71B1), latency from GameCharAgent
		// (0xA9ECB95D).
		private static readonly byte[] PingResponseSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameWorldType, 0x08);

		private static byte[] LoginWorld201CReceiver(INetworkClient client)
		{
			return ConanEndpoints.EndpointBytes(ConanEndpoints.GameSpawnEndpointType, client.Session.Account.ClientInstance);
		}

		private static byte[] LoginWorldReceiver(INetworkClient client)
		{
			return WorldFraming.Receiver(client);
		}
	}
}
