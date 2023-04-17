using System.Collections.Generic;
using System.Linq;

namespace Faolan.PacketAnalyzer
{
	public sealed class PacketAnalyzerPortSettings
	{
		public static PacketAnalyzerPortSettings Default { get; } = new PacketAnalyzerPortSettings();
		public static PacketAnalyzerPortSettings Empty { get; } = new PacketAnalyzerPortSettings(0);
		public ushort AgentServerPort { get; }
		public ushort CsPlayerAgentPort { get; }
		public ushort GameServerPort { get; }
		public ushort PlayerAgentPort { get; }

		public ushort UniverseAgentPort { get; }

		public PacketAnalyzerPortSettings(ushort universeAgentPort = 7010, ushort playerAgentPort = 0, ushort csPlayerAgentPort = 0, ushort gameServerPort = 0, ushort agentServerPort = 0)
		{
			UniverseAgentPort = universeAgentPort;
			PlayerAgentPort = playerAgentPort;
			CsPlayerAgentPort = csPlayerAgentPort;
			AgentServerPort = agentServerPort;
			GameServerPort = gameServerPort;
		}

		public IEnumerable<KeyValuePair<ushort, string>> GetKnownPortProtocols()
		{
			if (UniverseAgentPort > 0)
				yield return new KeyValuePair<ushort, string>(UniverseAgentPort, PacketProtocolInfo.UniverseAgentProtocolName);

			if (PlayerAgentPort > 0)
				yield return new KeyValuePair<ushort, string>(PlayerAgentPort, PacketProtocolInfo.PlayerAgentProtocolName);

			if (CsPlayerAgentPort > 0)
				yield return new KeyValuePair<ushort, string>(CsPlayerAgentPort, PacketProtocolInfo.CsPlayerAgentProtocolName);

			if (AgentServerPort > 0)
				yield return new KeyValuePair<ushort, string>(AgentServerPort, PacketProtocolInfo.AgentServerProtocolName);

			if (GameServerPort > 0)
				yield return new KeyValuePair<ushort, string>(GameServerPort, PacketProtocolInfo.GameServerProtocolName);
		}

		public static bool TryParse(string universeAgentPortText, string playerAgentPortText, string csPlayerAgentPortText, string agentServerPortText, string gameServerPortText, out PacketAnalyzerPortSettings settings,
			out string? errorMessage)
		{
			settings = Default;
			errorMessage = null;

			if (!TryParsePort(universeAgentPortText, "UniverseAgent", out var universeAgentPort, out errorMessage) || !TryParsePort(playerAgentPortText, "PlayerAgent", out var playerAgentPort, out errorMessage) ||
			    !TryParsePort(csPlayerAgentPortText, "CsPlayerAgent", out var csPlayerAgentPort, out errorMessage) || !TryParsePort(agentServerPortText, "AgentServer", out var agentServerPort, out errorMessage) ||
			    !TryParseOptionalPort(gameServerPortText, "GameServer", out var gameServerPort, out errorMessage))
				return false;

			var configuredPorts = new[] { universeAgentPort, playerAgentPort, csPlayerAgentPort, agentServerPort, gameServerPort };
			var uniquePorts = new HashSet<ushort>(configuredPorts.Where(port => port > 0));

			if (uniquePorts.Count != configuredPorts.Count(port => port > 0))
			{
				var duplicatePorts = new Dictionary<ushort, List<string>>();
				AddDuplicateLabel(duplicatePorts, universeAgentPort, "UniverseAgent");
				AddDuplicateLabel(duplicatePorts, playerAgentPort, "PlayerAgent");
				AddDuplicateLabel(duplicatePorts, csPlayerAgentPort, "CsPlayerAgent");
				AddDuplicateLabel(duplicatePorts, agentServerPort, "AgentServer");
				AddDuplicateLabel(duplicatePorts, gameServerPort, "GameServer");

				var duplicateDescription = string.Join(", ", duplicatePorts.Where(p => p.Value.Count > 1).Select(p => $"{p.Key} ({string.Join(" / ", p.Value)})"));
				errorMessage = $"Each configured packet analyzer port must be unique. Duplicate assignments: {duplicateDescription}";
				return false;
			}

			settings = new PacketAnalyzerPortSettings(universeAgentPort, playerAgentPort, csPlayerAgentPort, gameServerPort, agentServerPort);
			return true;
		}

		private static void AddDuplicateLabel(Dictionary<ushort, List<string>> duplicatePorts, ushort port, string label)
		{
			if (port == 0)
				return;

			if (!duplicatePorts.TryGetValue(port, out var labels))
			{
				labels = [];
				duplicatePorts[port] = labels;
			}

			labels.Add(label);
		}

		private static IEnumerable<PortOption> GetCandidateOptions(PortCandidate candidate)
		{
			if (candidate.DetectedPort > 0)
				yield return new PortOption(candidate.DetectedPort, false);

			if (candidate.FallbackPort > 0 && candidate.FallbackPort != candidate.DetectedPort)
				yield return new PortOption(candidate.FallbackPort, true);
		}

		private static bool TryParseOptionalPort(string value, string displayName, out ushort port, out string? errorMessage)
		{
			port = 0;
			errorMessage = null;

			if (string.IsNullOrWhiteSpace(value))
				return true;

			if (!ushort.TryParse(value.Trim(), out port) || port == 0)
			{
				errorMessage = $"{displayName} port must be blank or a number between 1 and {ushort.MaxValue}.";
				return false;
			}

			return true;
		}

		private static bool TryParsePort(string value, string displayName, out ushort port, out string? errorMessage)
		{
			port = 0;
			errorMessage = null;

			if (string.IsNullOrWhiteSpace(value))
			{
				errorMessage = $"{displayName} port is required.";
				return false;
			}

			if (!ushort.TryParse(value.Trim(), out port) || port == 0)
			{
				errorMessage = $"{displayName} port must be a number between 1 and {ushort.MaxValue}.";
				return false;
			}

			return true;
		}

		private static bool TryResolveCandidates(PortCandidate[] candidates, int index, ushort[] resolvedPorts, bool[] usedFallback, HashSet<ushort> usedPorts)
		{
			if (index >= candidates.Length)
				return true;

			var candidate = candidates[index];
			foreach (var option in GetCandidateOptions(candidate))
			{
				if (option.Port == 0 || !usedPorts.Add(option.Port))
					continue;

				resolvedPorts[index] = option.Port;
				usedFallback[index] = option.IsFallback;

				if (TryResolveCandidates(candidates, index + 1, resolvedPorts, usedFallback, usedPorts))
					return true;

				usedPorts.Remove(option.Port);
			}

			return false;
		}

		private readonly record struct PortCandidate(string Label, ushort DetectedPort, ushort FallbackPort);

		private readonly record struct PortOption(ushort Port, bool IsFallback);
	}
}
