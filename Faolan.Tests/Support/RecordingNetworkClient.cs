using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;

namespace Faolan.Tests.Support
{
	// A fake INetworkClient that records every packet sent, for golden-byte tests of the server's burst senders.
	// The bursts produce byte-exact retail packets that the client is proven to accept, so converting a verbatim
	// blob to explicit WriteHeader()/Write*() calls MUST be byte-identical - these tests lock that.
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
			Session.Character = new Character
			{
				Id = clientInstance
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
			var total = 0;
			foreach (var p in Sent)
				total += p.Length;

			var sb = new StringBuilder(total * 2);
			foreach (var p in Sent)
			foreach (var b in p)
				sb.Append(b.ToString("x2"));

			return sb.ToString();
		}
	}
}
