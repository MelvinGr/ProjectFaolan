namespace Faolan.Core.Network.Server.AgentServer
{
	/// <summary>
	///     Read-only snapshot of one connected client session, as surfaced to out-of-band
	///     tooling (e.g. the in-process MCP server). Decoupled from the live INetworkClient
	///     so callers can't mutate connection state.
	/// </summary>
	public sealed record SessionInfo(string IpAddress, uint? AccountId, string? AccountName, uint ClientInstance, uint? CharacterId, string? CharacterName, ushort? CharacterLevel);
}
