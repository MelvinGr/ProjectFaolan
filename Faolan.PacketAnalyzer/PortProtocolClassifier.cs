using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.PacketAnalyzer
{
	// Classifies which protocol each server ENDPOINT (ip:port) speaks, purely from packet CONTENT.
	//
	// Classification is per endpoint, not per port, because a single capture can contain two different
	// servers that reuse the same port (e.g. a login host on 37.18.192.43:7012 running CsPlayerAgent and
	// a game host on 37.18.193.22:7012 running GameServer). A port-only classifier conflates them and, by
	// labelling the game endpoint as CsPlayerAgent, makes the reader skip decompression for it and drop
	// its (compressed) downloads.
	//
	// Each protocol is identified by the opcodes UNIQUE to it among the four server-side protocols
	// (PlayerAgent, CsPlayerAgent, AgentServer, GameServer). Shared opcodes (e.g. 0x2000) are ignored. An
	// endpoint that carries more than one protocol's distinctive opcodes is resolved by priority:
	// AgentServer > PlayerAgent > CsPlayerAgent > GameServer.
	internal sealed class PortProtocolClassifier
	{
		private const int AgentServerClaimThreshold = 1;

		// Minimum distinctive opcode hits before a protocol may claim an endpoint (filters spurious hits
		// from mis-framed/compressed payloads). AgentServer uses inline framing where only its opening
		// Hello (0x0000, unique to it) decodes cleanly, so one hit is enough.
		private const int DefaultClaimThreshold = 2;

		private static readonly string[] ProtocolPriority =
		[
			PacketProtocolInfo.AgentServerProtocolName,
			PacketProtocolInfo.PlayerAgentProtocolName,
			PacketProtocolInfo.CsPlayerAgentProtocolName,
			PacketProtocolInfo.GameServerProtocolName
		];

		private static readonly IReadOnlyDictionary<string, HashSet<uint>> DistinctiveOpcodes = BuildDistinctiveOpcodes();

		// Highest priority first.

		private readonly Dictionary<string, EndpointVotes> _votesByEndpoint = [];

		private readonly ushort _universeAgentPort;

		public PortProtocolClassifier(ushort universeAgentPort)
		{
			_universeAgentPort = universeAgentPort;
		}

		public void Observe(string serverIpAddress, ushort serverPort, uint opcode)
		{
			// UniverseAgent's port is fixed and shares opcodes (0x2000/0x2001/0x2005) with other
			// protocols, so never let its traffic vote.
			if (serverPort == 0 || serverPort == _universeAgentPort || string.IsNullOrEmpty(serverIpAddress))
				return;

			var key = $"{serverIpAddress}:{serverPort}";
			if (!_votesByEndpoint.TryGetValue(key, out var endpoint))
				_votesByEndpoint[key] = endpoint = new EndpointVotes(serverIpAddress, serverPort);

			foreach (var (protocolName, opcodes) in DistinctiveOpcodes)
			{
				if (!opcodes.Contains(opcode))
					continue;

				endpoint.Votes.TryGetValue(protocolName, out var count);
				endpoint.Votes[protocolName] = count + 1;
			}
		}

		public ClassificationResult Resolve()
		{
			// Per endpoint, pick the winning protocol (highest priority past its threshold).
			var endpointProtocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (key, endpoint) in _votesByEndpoint)
			{
				var winner = ProtocolPriority.FirstOrDefault(protocol => endpoint.Votes.TryGetValue(protocol, out var count) && count >= ClaimThreshold(protocol));
				if (winner != null)
					endpointProtocols[key] = winner;
			}

			// Representative port per protocol (the endpoint with the strongest support), for the summary.
			ushort PortFor(string protocolName)
			{
				return endpointProtocols
					.Where(entry => entry.Value == protocolName)
					.OrderByDescending(entry => _votesByEndpoint[entry.Key]
						.Votes[protocolName])
					.Select(entry => _votesByEndpoint[entry.Key].Port)
					.FirstOrDefault();
			}

			var settings = new PacketAnalyzerPortSettings(_universeAgentPort, PortFor(PacketProtocolInfo.PlayerAgentProtocolName), PortFor(PacketProtocolInfo.CsPlayerAgentProtocolName),
				PortFor(PacketProtocolInfo.GameServerProtocolName), PortFor(PacketProtocolInfo.AgentServerProtocolName));

			return new ClassificationResult(settings, endpointProtocols);
		}

		private static IReadOnlyDictionary<string, HashSet<uint>> BuildDistinctiveOpcodes()
		{
			var combined = new Dictionary<string, HashSet<uint>>
			{
				[PacketProtocolInfo.PlayerAgentProtocolName] = OpcodesOf(typeof(PlayerAgentOpcodes), typeof(PlayerAgentResponseOpcodes)),
				[PacketProtocolInfo.CsPlayerAgentProtocolName] = OpcodesOf(typeof(CsPlayerAgentOpcodes), typeof(CsPlayerAgentResponseOpcodes)),
				[PacketProtocolInfo.AgentServerProtocolName] = OpcodesOf(typeof(AgentServerOpcodes), typeof(AgentServerRespondsOpcodes)),
				[PacketProtocolInfo.GameServerProtocolName] = OpcodesOf(typeof(GameServerOpcodes), typeof(GameServerRespondOpcodes))
			};

			var distinctive = new Dictionary<string, HashSet<uint>>();
			foreach (var (protocolName, opcodes) in combined)
			{
				var unique = new HashSet<uint>(opcodes);
				foreach (var (otherName, otherOpcodes) in combined)
				{
					if (otherName != protocolName)
						unique.ExceptWith(otherOpcodes);
				}

				distinctive[protocolName] = unique;
			}

			return distinctive;
		}

		private static int ClaimThreshold(string protocolName)
		{
			return protocolName == PacketProtocolInfo.AgentServerProtocolName
				? AgentServerClaimThreshold
				: DefaultClaimThreshold;
		}

		private static HashSet<uint> OpcodesOf(params Type[] enumTypes)
		{
			var opcodes = new HashSet<uint>();
			foreach (var enumType in enumTypes)
			{
				foreach (var value in Enum.GetValues(enumType))
					opcodes.Add(Convert.ToUInt32(value));
			}

			return opcodes;
		}

		private sealed class EndpointVotes(string ipAddress, ushort port)
		{
			public Dictionary<string, int> Votes { get; } = [];
			public string IpAddress { get; } = ipAddress;
			public ushort Port { get; } = port;
		}

		// settings = representative per-protocol ports (for the summary); EndpointProtocols maps each
		// "ip:port" to its resolved protocol (authoritative per endpoint, handles ports shared across hosts).
		public readonly record struct ClassificationResult(PacketAnalyzerPortSettings Settings, IReadOnlyDictionary<string, string> EndpointProtocols);
	}
}
