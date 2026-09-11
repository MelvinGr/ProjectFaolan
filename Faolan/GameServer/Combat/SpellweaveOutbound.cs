using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The spellweaving FSM's wire and world-state surface: one explicit forward per member to the service that
	///     owns the act - the energy loop, the buff registry, the world model and the chat dispatcher.
	/// </summary>
	/// <remarks>
	///     A class rather than a facade on the listener, for the same reason as every other outbound port: none of
	///     these members needs anything the listener owns.
	/// </remarks>
	public sealed class SpellweaveOutbound : ISpellweaveOutbound
	{
		private readonly BuffService _buffs;
		private readonly IChatServerDispatcher _chat;
		private readonly EnergyService _energy;
		private readonly WorldSimulation _simulation;

		public SpellweaveOutbound(WorldSimulation simulation, EnergyService energy, BuffService buffs, IChatServerDispatcher chat)
		{
			_simulation = simulation;
			_energy = energy;
			_buffs = buffs;
			_chat = chat;
		}

		public bool IsMounted(INetworkClient client)
		{
			return _simulation.PlayerWorldEntity(client)?.IsMounted == true;
		}

		public void EnsureEnergyLoop(INetworkClient client)
		{
			_energy.EnsureEnergyLoop(client);
		}

		public Task ApplyBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return _buffs.ApplyBuffById(client, applyTo, applyFrom, wireSpellId);
		}

		public Task RemoveBuff(INetworkClient client, uint applyTo, uint applyFrom, uint wireSpellId)
		{
			return _buffs.SendBuffRemoval(client, applyTo, applyFrom, wireSpellId);
		}

		public Task SendFeedback(INetworkClient client, string message)
		{
			return _chat.SendSystemMessage(client.Session.Character.Id, message);
		}

		public Task SendMountedRefusal(INetworkClient client)
		{
			return GameWire.SendWorldChat(client, ChatTextXml.MustNotHaveMountRunning);
		}

		public Task SendSprintBar(INetworkClient client, uint energy)
		{
			return EnergyService.SendSprintBar(client, energy);
		}
	}
}
