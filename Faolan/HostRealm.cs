namespace Faolan
{
	/// <summary>
	///     The realm THIS process hosts (#324), resolved once in the pre-host bootstrap and registered as a singleton so
	///     the realm-local services take their ports by injection rather than through a factory lambda.
	/// </summary>
	/// <remarks>
	///     Deliberately NOT the <c>Realm</c> entity: that row is loaded before the host exists and is therefore detached
	///     from a disposed context, so registering it would put a mutable EF entity in the container for the process
	///     lifetime. Nothing here needs the entity - the listeners only ever read a port, and the one place that wants
	///     the live row (<c>SendWorldHandoff</c>) does its own fresh <c>GetRealm</c> read.
	/// </remarks>
	public sealed record HostRealm(uint Id, string Name, ushort Port, ushort ChatServerPort, ushort CsPlayerAgentPort);
}
