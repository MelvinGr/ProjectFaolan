using System;
using System.Collections.Generic;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.PacketAnalyzer
{
	internal sealed class PacketProtocolDetector
	{
		private readonly bool _allowProtocolInference;
		private readonly Dictionary<string, Queue<string>> _pendingPlayerAgentAdvertisements = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, string> _endpointProtocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, ushort> _authoritativeProtocolPorts = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, ushort> _protocolPorts = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<ushort, string> _authoritativePortProtocols = new Dictionary<ushort, string>();
		private readonly Dictionary<ushort, string> _portProtocols = new Dictionary<ushort, string>();

		// Endpoints (ip:port) whose protocol was resolved authoritatively by the content classifier. These
		// win over per-port lookups and must not be overridden by per-packet inference - this is what lets
		// two servers that share a port (e.g. login-host:7012 CsPlayerAgent vs game-host:7012 GameServer)
		// be labelled correctly, so the game endpoint's compressed downloads get decompressed.
		private readonly HashSet<string> _authoritativeEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<ushort> _knownPorts = [];
		private readonly PacketAnalyzerPortSettings _portSettings;

		public PacketProtocolDetector(PacketAnalyzerPortSettings? portSettings = null, bool allowProtocolInference = true, IReadOnlyDictionary<string, string>? endpointProtocols = null)
		{
			_allowProtocolInference = allowProtocolInference;
			_portSettings = portSettings ?? PacketAnalyzerPortSettings.Default;

			foreach (var portProtocol in _portSettings.GetKnownPortProtocols())
				RegisterPortProtocol(portProtocol.Key, portProtocol.Value, true);

			if (endpointProtocols == null)
				return;

			foreach (var (endpointKey, protocolName) in endpointProtocols)
			{
				var separatorIndex = endpointKey.LastIndexOf(':');
				if (separatorIndex <= 0 || !ushort.TryParse(endpointKey[(separatorIndex + 1)..], out var port))
					continue;

				_endpointProtocols[endpointKey] = protocolName;
				_authoritativeEndpoints.Add(endpointKey);
				_knownPorts.Add(port);
			}
		}

		public string? GetKnownProtocol(string ipAddress, ushort port)
		{
			return TryGetKnownProtocol(ipAddress, port);
		}

		// Forces a specific server endpoint to GameServer. Called when the GameServer zlib-stream marker
		// (0x80000005) is detected on its download stream. Content detection beats port classification here: the
		// world GameServer moves to a fresh port on every playfield change, and the same port number can be a
		// different service on another server IP - so a port-based label mislabels these streams (and the importer
		// then drops them). Endpoint-scoped (IP:port) and authoritative so later inference can't override it.
		public void MarkGameServerEndpoint(string ipAddress, ushort port)
		{
			var key = CreateEndpointKey(ipAddress, port);
			_endpointProtocols[key] = PacketProtocolInfo.GameServerProtocolName;
			_authoritativeEndpoints.Add(key);
			_knownPorts.Add(port);
		}

		public DetectedService ObservePacket(string sourceAddress, string destinationAddress, string sourceIpAddress, string destinationIpAddress, ushort sourcePort, ushort destinationPort, bool isSourceLocal,
			bool isDestinationLocal, bool isDownload, ConanPacket packet)
		{
			var sourceKey = CreateEndpointKey(sourceIpAddress, sourcePort);
			var destinationKey = CreateEndpointKey(destinationIpAddress, destinationPort);

			ObserveUniverseAdvertisement(sourceKey, sourcePort, isDownload, packet);

			MaybeRegisterUniverseAgent(sourceKey, sourcePort);
			MaybeRegisterUniverseAgent(destinationKey, destinationPort);
			PromoteKnownPortProtocol(sourceKey, sourcePort);
			PromoteKnownPortProtocol(destinationKey, destinationPort);

			var serverEndpoint = SelectServerEndpoint(sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort, destinationPort, isSourceLocal, isDestinationLocal, isDownload);

			var protocolName = TryGetKnownProtocol(serverEndpoint.IpAddress, serverEndpoint.Port);
			var canOverrideProtocol = !_authoritativeEndpoints.Contains(serverEndpoint.Key) &&
			                          !(serverEndpoint.Port == _portSettings.UniverseAgentPort && string.Equals(protocolName, PacketProtocolInfo.UniverseAgentProtocolName, StringComparison.OrdinalIgnoreCase));

			if (canOverrideProtocol && _allowProtocolInference && PacketProtocolInfo.TryInferProtocolName(packet, isDownload, out var inferredProtocolName) && CanApplyInferredProtocol(serverEndpoint.Port, inferredProtocolName) &&
			    !string.Equals(protocolName, inferredProtocolName, StringComparison.OrdinalIgnoreCase))
			{
				protocolName = inferredProtocolName;
				RegisterProtocol(serverEndpoint.Key, serverEndpoint.Port, protocolName);
			}

			if (string.Equals(protocolName, PacketProtocolInfo.PlayerAgentProtocolName, StringComparison.OrdinalIgnoreCase))
				ObservePlayerAgent(serverEndpoint.Key, isDownload, packet);

			PromoteKnownPortProtocol(sourceKey, sourcePort);
			PromoteKnownPortProtocol(destinationKey, destinationPort);

			serverEndpoint = SelectServerEndpoint(sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort, destinationPort, isSourceLocal, isDestinationLocal, isDownload);

			protocolName = TryGetKnownProtocol(serverEndpoint.IpAddress, serverEndpoint.Port) ?? protocolName ?? (_allowProtocolInference
				? PacketProtocolInfo.GetProtocolName(serverEndpoint.Port, isDownload, packet.Opcode)
				: PacketProtocolInfo.UnknownProtocolName);

			if (!string.Equals(protocolName, PacketProtocolInfo.UnknownProtocolName, StringComparison.OrdinalIgnoreCase) && !protocolName.StartsWith("Port ", StringComparison.Ordinal))
				RegisterProtocol(serverEndpoint.Key, serverEndpoint.Port, protocolName);

			return new DetectedService(CleanDisplayAddress(serverEndpoint.Address), protocolName);
		}

		public DetectedService ResolveService(string sourceAddress, string destinationAddress, string sourceIpAddress, string destinationIpAddress, ushort sourcePort, ushort destinationPort, bool isSourceLocal,
			bool isDestinationLocal, bool isDownload)
		{
			MaybeRegisterUniverseAgent(CreateEndpointKey(sourceIpAddress, sourcePort), sourcePort);
			MaybeRegisterUniverseAgent(CreateEndpointKey(destinationIpAddress, destinationPort), destinationPort);
			PromoteKnownPortProtocol(CreateEndpointKey(sourceIpAddress, sourcePort), sourcePort);
			PromoteKnownPortProtocol(CreateEndpointKey(destinationIpAddress, destinationPort), destinationPort);

			var serverEndpoint = SelectServerEndpoint(sourceAddress, destinationAddress, sourceIpAddress, destinationIpAddress, sourcePort, destinationPort, isSourceLocal, isDestinationLocal, isDownload);
			var protocolName = TryGetKnownProtocol(serverEndpoint.IpAddress, serverEndpoint.Port) ?? PacketProtocolInfo.GetProtocolName(serverEndpoint.Port, isDownload);

			if (!string.Equals(protocolName, PacketProtocolInfo.UnknownProtocolName, StringComparison.OrdinalIgnoreCase) && !protocolName.StartsWith("Port ", StringComparison.Ordinal))
				RegisterProtocol(serverEndpoint.Key, serverEndpoint.Port, protocolName);

			return new DetectedService(CleanDisplayAddress(serverEndpoint.Address), protocolName);
		}

		public bool ShouldInspect(string sourceIpAddress, ushort sourcePort, string destinationIpAddress, ushort destinationPort)
		{
			return TryGetKnownProtocol(sourceIpAddress, sourcePort) != null || TryGetKnownProtocol(destinationIpAddress, destinationPort) != null || _knownPorts.Contains(sourcePort) || _knownPorts.Contains(destinationPort);
		}

		public bool TryGetPort(string protocolName, out ushort port)
		{
			return _protocolPorts.TryGetValue(protocolName, out port);
		}

		private bool CanApplyInferredProtocol(ushort port, string protocolName)
		{
			if (_authoritativeProtocolPorts.TryGetValue(protocolName, out var authoritativeProtocolPort) && authoritativeProtocolPort != port)
				return false;

			return !_authoritativePortProtocols.TryGetValue(port, out var authoritativePortProtocol) || string.Equals(authoritativePortProtocol, protocolName, StringComparison.OrdinalIgnoreCase);
		}

		private string? DequeuePendingRole(string playerAgentEndpointKey)
		{
			if (!_pendingPlayerAgentAdvertisements.TryGetValue(playerAgentEndpointKey, out var queue))
				return null;

			return queue.Count > 0
				? queue.Dequeue()
				: null;
		}

		private void MaybeRegisterUniverseAgent(string endpointKey, ushort port)
		{
			if (port != _portSettings.UniverseAgentPort)
				return;

			RegisterProtocol(endpointKey, port, PacketProtocolInfo.UniverseAgentProtocolName);
		}

		private void ObservePlayerAgent(string playerAgentEndpointKey, bool isDownload, ConanPacket packet)
		{
			if (!isDownload)
			{
				if (packet.Opcode == (ushort)PlayerAgentOpcodes.Ox20EC)
				{
					_pendingPlayerAgentAdvertisements[playerAgentEndpointKey] = new Queue<string>([
						PacketProtocolInfo.AgentServerProtocolName,
						PacketProtocolInfo.CsPlayerAgentProtocolName,
						PacketProtocolInfo.GameServerProtocolName
					]);
				}

				return;
			}

			if (packet.Opcode != (uint)PlayerAgentResponseOpcodes.Ox1A07 || packet.Body == null)
				return;

			var payload = packet.Body.ToArray();
			if (!PacketAdvertisedEndpointParser.TryExtractEndpointPort(payload, out var advertisedPort))
				return;

			var protocolName = DequeuePendingRole(playerAgentEndpointKey);
			if (protocolName == null)
				return;

			RegisterPortProtocol(advertisedPort, protocolName, true);
		}

		private void ObserveUniverseAdvertisement(string sourceKey, ushort sourcePort, bool isDownload, ConanPacket packet)
		{
			// UniverseAgent SendPlayerAgentRealm and GameServer AckAuthentication share opcode 0x2001. Distinguish
			// them by the body: only the Universe advertisement carries a "host:port" PlayerAgent address, so we
			// require that to parse before treating this as a Universe advertisement. This both lets unconfigured
			// (Empty) settings bootstrap detection from the advertisement AND keeps a GameServer's AckAuthentication
			// (no host:port body) from being mistaken for one and relabeling the game endpoint as UniverseAgent
			// (which would stop its downloads from being decompressed).
			if (!isDownload || packet.Opcode != (uint)UniverseAgentResponseOpcodes.SendPlayerAgentRealm || packet.Body == null)
				return;

			// When the UniverseAgent port is known, additionally require the packet to come from it so a game
			// endpoint can never relabel itself; when ports are unconfigured the host:port parse alone gates it.
			if (_portSettings.UniverseAgentPort != 0 && sourcePort != _portSettings.UniverseAgentPort)
				return;

			if (!PacketAdvertisedEndpointParser.TryFindHostPort(packet.Body.ToArray(), out var port))
				return;

			RegisterProtocol(sourceKey, sourcePort, PacketProtocolInfo.UniverseAgentProtocolName);
			RegisterPortProtocol(port, PacketProtocolInfo.PlayerAgentProtocolName, true);
		}

		private void PromoteKnownPortProtocol(string endpointKey, ushort port)
		{
			if (_endpointProtocols.ContainsKey(endpointKey) || !_portProtocols.TryGetValue(port, out var protocolName))
				return;

			_endpointProtocols[endpointKey] = protocolName;
		}

		private void RegisterPortProtocol(ushort port, string protocolName, bool isAuthoritative = false)
		{
			_knownPorts.Add(port);

			if (_authoritativePortProtocols.TryGetValue(port, out var authoritativePortProtocol) && !string.Equals(authoritativePortProtocol, protocolName, StringComparison.OrdinalIgnoreCase))
				return;

			if (_authoritativeProtocolPorts.TryGetValue(protocolName, out var authoritativeProtocolPort) && authoritativeProtocolPort != port)
				return;

			if (isAuthoritative)
			{
				_authoritativeProtocolPorts[protocolName] = port;
				_authoritativePortProtocols[port] = protocolName;
			}

			_protocolPorts[protocolName] = port;

			if (_portProtocols.TryGetValue(port, out var existingProtocolName) && string.Equals(existingProtocolName, protocolName, StringComparison.OrdinalIgnoreCase))
				return;

			_portProtocols[port] = protocolName;
		}

		private void RegisterProtocol(string endpointKey, ushort port, string protocolName)
		{
			_endpointProtocols[endpointKey] = protocolName;
			RegisterPortProtocol(port, protocolName);
		}

		private EndpointObservation SelectServerEndpoint(string sourceAddress, string destinationAddress, string sourceIpAddress, string destinationIpAddress, ushort sourcePort, ushort destinationPort, bool isSourceLocal,
			bool isDestinationLocal, bool isDownload)
		{
			var sourceKnown = TryGetKnownProtocol(sourceIpAddress, sourcePort) != null;
			var destinationKnown = TryGetKnownProtocol(destinationIpAddress, destinationPort) != null;

			if (sourceKnown && !destinationKnown)
				return new EndpointObservation(CreateEndpointKey(sourceIpAddress, sourcePort), sourceAddress, sourceIpAddress, sourcePort);

			if (destinationKnown && !sourceKnown)
				return new EndpointObservation(CreateEndpointKey(destinationIpAddress, destinationPort), destinationAddress, destinationIpAddress, destinationPort);

			if (sourcePort == _portSettings.UniverseAgentPort)
				return new EndpointObservation(CreateEndpointKey(sourceIpAddress, sourcePort), sourceAddress, sourceIpAddress, sourcePort);

			if (destinationPort == _portSettings.UniverseAgentPort)
				return new EndpointObservation(CreateEndpointKey(destinationIpAddress, destinationPort), destinationAddress, destinationIpAddress, destinationPort);

			if (!isSourceLocal && isDestinationLocal)
				return new EndpointObservation(CreateEndpointKey(sourceIpAddress, sourcePort), sourceAddress, sourceIpAddress, sourcePort);

			if (!isDestinationLocal && isSourceLocal)
				return new EndpointObservation(CreateEndpointKey(destinationIpAddress, destinationPort), destinationAddress, destinationIpAddress, destinationPort);

			var sourcePortKnown = _portProtocols.ContainsKey(sourcePort);
			var destinationPortKnown = _portProtocols.ContainsKey(destinationPort);

			if (sourcePortKnown && !destinationPortKnown)
				return new EndpointObservation(CreateEndpointKey(sourceIpAddress, sourcePort), sourceAddress, sourceIpAddress, sourcePort);

			if (destinationPortKnown && !sourcePortKnown)
				return new EndpointObservation(CreateEndpointKey(destinationIpAddress, destinationPort), destinationAddress, destinationIpAddress, destinationPort);

			return isDownload
				? new EndpointObservation(CreateEndpointKey(sourceIpAddress, sourcePort), sourceAddress, sourceIpAddress, sourcePort)
				: new EndpointObservation(CreateEndpointKey(destinationIpAddress, destinationPort), destinationAddress, destinationIpAddress, destinationPort);
		}

		private string? TryGetKnownProtocol(string ipAddress, ushort port)
		{
			var endpointKey = CreateEndpointKey(ipAddress, port);
			if (_endpointProtocols.TryGetValue(endpointKey, out var endpointProtocolName))
				return endpointProtocolName;

			return _portProtocols.TryGetValue(port, out var portProtocolName)
				? portProtocolName
				: null;
		}

		private static string CleanDisplayAddress(string address)
		{
			return address.Replace(".ageofconan.com", "", StringComparison.OrdinalIgnoreCase);
		}

		private static string CreateEndpointKey(string ipAddress, ushort port)
		{
			return $"{ipAddress}:{port}";
		}

		internal readonly record struct DetectedService(string ServiceName, string ProtocolName);

		private readonly record struct EndpointObservation(string Key, string Address, string IpAddress, ushort Port);
	}
}
