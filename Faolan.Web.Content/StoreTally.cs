using System.Threading;

namespace Faolan.Web.Content
{
	// One upstream tag's share of the mirror store: how many blobs it holds and how many bytes they occupy.
	// Both numbers answer the same question - how complete the archive is - so they are counted together and mutated in
	// place: the tally is created once per tag by the startup scan and then kept live by the store path, which reaches
	// it through a ConcurrentDictionary entry that is never replaced.
	internal sealed class StoreTally
	{
		private long _bytes;
		private long _files;

		internal StoreTally(long files = 0, long bytes = 0)
		{
			_files = files;
			_bytes = bytes;
		}

		internal long Files => Interlocked.Read(ref _files);
		internal long Bytes => Interlocked.Read(ref _bytes);

		// One newly stored blob of `bytes` bytes. Interlocked because concurrent misses on different paths land
		// here at the same time - the single-flight gate is per path, not per tag.
		internal void Add(long bytes)
		{
			Interlocked.Increment(ref _files);
			Interlocked.Add(ref _bytes, bytes);
		}
	}
}
