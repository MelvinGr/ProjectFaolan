using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Core.Network.Server.CsPlayerAgent
{
	public interface ICsPlayerAgentDispatcher : ICsPlayerAgentProvider
	{
		void Register(ICsPlayerAgentProvider provider);
		void Unregister(ICsPlayerAgentProvider provider);
	}

	/// <summary>
	///     The <see cref="ProviderDispatcher{TProvider}" /> slot for the CsPlayerAgent listener: every op forwards to the
	///     one registered listener, or answers false when none is running (a login-only process has no CS tier).
	/// </summary>
	public sealed class CsPlayerAgentDispatcher : ProviderDispatcher<ICsPlayerAgentProvider>, ICsPlayerAgentDispatcher
	{
		public async Task<bool> LeaveTeam(uint characterId)
		{
			var provider = Provider;
			return provider != null && await provider.LeaveTeam(characterId);
		}

		public async Task<bool> KickFromTeam(uint leaderId, uint targetId)
		{
			var provider = Provider;
			return provider != null && await provider.KickFromTeam(leaderId, targetId);
		}

		public IReadOnlyList<uint> TeamMembersOf(uint characterId)
		{
			return Provider?.TeamMembersOf(characterId) ?? [];
		}
	}
}
