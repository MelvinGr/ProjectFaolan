using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Protocol;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The one place a world IIR goes onto a client's world connection, as a DispatchPassBlob.
	/// </summary>
	/// <remarks>
	///     The u32 length belongs to the sender, so a codec emits a bare record and there is exactly one way to put it
	///     on the wire. A second sender that took an already-framed record would be indistinguishable at a call site,
	///     and getting it the wrong way round puts the IIR type where the client reads a length - which it then reads
	///     as a payload of hundreds of megabytes and drops the connection over.
	/// </remarks>
	public static class WorldIir
	{
		/// <summary>
		///     Sends one record: <paramref name="record" /> is [u32 iirType][record], and the carrier's [u32 len] is
		///     written here.
		/// </summary>
		public static Task Send(INetworkClient client, params byte[] record)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));
		}
	}
}
