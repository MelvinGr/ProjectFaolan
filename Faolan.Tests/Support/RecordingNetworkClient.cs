using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;

namespace Faolan.Tests.Support
{
	// A fake INetworkClient that records every packet sent, for golden-byte tests of the server's burst senders.
	// The bursts produce byte-exact retail packets that the client is proven to accept, so converting a verbatim
	// blob to explicit WriteHeader()/Write*() calls must be byte-identical - these tests lock that.
	public sealed class RecordingNetworkClient : INetworkClient
	{
		public INetworkClientSession Session { get; } = new NetworkClientSession();
		public List<byte[]> Sent { get; } = [];
		public string IpAddress => "127.0.0.1";

		public RecordingNetworkClient(uint clientInstance)
		{
			Session.Account = new Account
			{
				ClientInstance = clientInstance
			};
			// For a logged-in player handle == ClientInstance == Character.Id, so mirror that: world-entry code
			// reads Character.Id. Tests that need more character fields replace or mutate this.
			//
			// Level 1 and a real class because a Character is never legitimately at level 0 - creation allocates at 1,
			// and the class-derived paths (e.g. the create editor's RDB 1000511 base push) throw on a 0 rather than
			// inventing a curve. A default-constructed stand-in would fail those for a reason that says nothing about
			// the behaviour under test.
			Session.Character = new Character
			{
				Id = clientInstance,
				Level = 1,
				Class = ConanClass.Barbarian
			};
		}

		public Task SendAsync(byte[] value, CancellationToken cancellationToken = default)
		{
			Sent.Add(value);
			return Task.CompletedTask;
		}

		public void Start()
		{
			throw new NotSupportedException();
		}

		public void Stop()
		{
			throw new NotSupportedException();
		}

		// All sent packets concatenated, as one lowercase hex string - the golden form.
		public string SentHex()
		{
			byte[] all = [.. Sent.SelectMany(p => p)];
			return Convert.ToHexStringLower(all);
		}
	}
}
