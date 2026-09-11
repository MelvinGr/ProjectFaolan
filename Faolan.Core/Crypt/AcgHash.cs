namespace Faolan.Core.Crypt
{
	/// <summary>
	///     The client's ACGHash: the 4-character token that identifies one item template on the wire
	///     (ProtoAcgItem.hash, stored harvested as Items.Hash).
	/// </summary>
	/// <remarks>
	///     A token namespace, the same family as the neighbours in this folder - <see cref="Bkdr31" /> turns a name
	///     into an id, <see cref="Elf28" />/<see cref="Crc32" /> a byte run into one; this one is the id space the
	///     client's own authoring pipeline hands out.
	///     <para />
	///     What it is, measured over the 3,993 harvested tokens and the 120 capture dumps:
	///     <list type="bullet">
	///         <item>
	///             four bytes, every one of them [0-9A-Z] - the charset the client's ACGHash stream reader
	///             FUN_005d7190 validates, and the only thing it checks: it compares the word against no expected
	///             value.
	///         </item>
	///         <item>
	///             per template and perfectly stable - 107,929 sightings across different accounts, characters and
	///             sessions produced 0 templates carrying two different tokens, and 0 carrying two identity quads.
	///         </item>
	///         <item>
	///             carrying no information about the item. Uniform at every position (chi2 34.9/39.0/42.4/42.4
	///             against a 49.8 critical value), no check digit, Spearman -0.015 against the template id, affine/LCG
	///             in the id rejected, and not any of crc32/adler32/fnv1a/bkdr31/djb2/md5/sha1 over the id/name/identity
	///             quad under either reduction (0/400 on all 84 combinations). It is an authoring-pipeline identity, not
	///             a computation, and it resolves to nothing in the client's RDB content either. So unlike every other
	///             hash in this folder, ACGHash has no algorithm to reproduce: the only operations that exist are
	///             "carry the harvested one" and "mint a fresh one".
	///         </item>
	///     </list>
	///     The client therefore has no oracle to validate a token against, and cannot tell a minted one from a retail
	///     one: in-client, an item given an unseen in-charset token ('ZZZZ') and a hash-less item given a minted one
	///     ('ZZZY') both render correctly - on the live DispatchAddItem path and, after a relog, through the
	///     world-entry snapshot.
	///     <para />
	///     A minted token must be in-charset. Sending a deliberately wrong hash such as a random u32 tests the charset
	///     validator, not the identity, and reads as "the client validates the hash, minting is impossible". Mint
	///     through <see cref="Mint" /> and nothing else.
	/// </remarks>
	public static class AcgHash
	{
		// The token alphabet, in base-36 digit order. The client's reader accepts these and '_'; no retail item token
		// uses an underscore (0 of 4,615), which is what makes it the minting space - see Mint.
		private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

		// 36^4 = 1,679,616 distinct tokens.
		public const uint Space = 36u * 36u * 36u * 36u;

		// The minting space: one of the four positions holds the '_', the other three are base-36 digits.
		// 4 x 36^3 = 186,624 tokens, every one of them disjoint from every harvested token by construction.
		public const uint MintSpace = 4u * 36u * 36u * 36u;

		// Re-rolls every minted token. A collision is not a correctness bug (the client resolves nothing through the
		// token) but two templates sharing an identity is a property retail maintains, so we maintain it too -
		// `--report acg-token-check` scores the shipped data and names the next clean salt.
		//
		// Minted tokens all carry a '_' and no harvested one does (see Mint), so the two populations cannot collide
		// at all. The only collisions possible are minted-vs-minted: 529^2/(2 x 186,624) = 0.75 expected per salt, a
		// term that shrinks as harvests cover more items, because every newly harvested template stops being minted
		// for.
		private const uint Salt = 0x14D6F0B6;

		/// <summary>The salt in force, so `--report acg-token-check` can sweep forward from it for a clean one.</summary>
		public const uint CurrentSalt = Salt;

		// The token to put on the wire for a template: the harvested one when a capture ever showed this template
		// entering an inventory, otherwise a minted one.
		//
		// Deliberately a named call rather than a property on Item: a synthesized value the caller cannot tell
		// apart from a real one is the failure mode the no-silent-fallback rule exists to catch, so the call site
		// says out loud that the token may be minted. Items.Hash keeps its exact meaning - the harvested value,
		// 0 when no capture has ever carried it - and stays the honest provenance record.
		public static uint ForWire(uint templateId, uint harvestedHash)
		{
			return harvestedHash != 0
				? harvestedHash
				: Mint(templateId);
		}

		// A deterministic, well-formed token for a template we have never harvested one for. Stable across
		// processes and sessions, so the live add and the next login's snapshot agree without storing anything.
		//
		// Every minted token carries an underscore, which is what makes minting disjoint from harvesting by
		// construction. Zero of the 4,615 harvested tokens contain one, so a minted token can never collide with a
		// retail one - it is not a smaller chance, it is impossible. It also makes a minted token identifiable after
		// the fact (`IsMinted`).
		//
		// '_' is not a byte the in-client rendering check exercised ('ZZZZ' and 'ZZZY' are both in [0-9A-Z]); what
		// backs it is the client's own validator, read out of the binary: `FUN_005d7190` stores the four bytes as a
		// u32 and then walks indices 0..3 with one predicate applied identically at every index -
		//
		//     ('/' < c && c < ':')  ||  ('@' < c && c < '[')  ||  c == '_'      // 0-9, A-Z, _
		//
		// - falling out of the loop into `Read invalid hash "..." from stream.`. There is no per-position branch, so
		// '_' is accepted in any of the four, first included. And `ACGHash` appears in that one function and nowhere
		// else in the image: the token is never decoded into digits, only stored and compared as an opaque word, so
		// no consumer can treat '_' arithmetically differently from any other byte.
		public static uint Mint(uint templateId)
		{
			return MintWith(templateId, Salt);
		}

		/// <summary>Whether a token was minted by us rather than harvested - i.e. whether it carries an underscore.</summary>
		/// <remarks>
		///     Sound in both directions only because no harvested token contains one (0 of 4,615) - it is a property of
		///     the shipped data, gated by <c>--report acg-token-check</c>, not of the format.
		/// </remarks>
		public static bool IsMinted(uint hash)
		{
			for (var i = 0; i < 4; i++)
			{
				if ((char)((hash >> (8 * i)) & 0xFF) == '_')
					return true;
			}

			return false;
		}

		/// <summary>
		///     <see cref="Mint" /> with an explicit salt, for the report that sweeps for a clean one. Production always
		///     goes through <see cref="Mint" />: a settable salt would let two processes mint different tokens for the
		///     same template, which is the one property minting has to keep.
		/// </summary>
		public static uint MintWith(uint templateId, uint salt)
		{
			// FNV-1a over the template id, reduced into the token space. The choice of mixer is arbitrary - the
			// retail value is uniformly random, so any good mixer is as faithful as another; what matters is that
			// it is deterministic and lands in-charset.
			var h = 0x811C9DC5u ^ salt;
			for (var i = 0; i < 4; i++)
			{
				h ^= (templateId >> (8 * i)) & 0xFF;
				h *= 0x01000193u;
			}

			// Which of the four positions carries the '_', and the base-36 value filling the other three. Splitting the
			// mixed word rather than reducing it once keeps both choices deterministic from the same hash.
			return EncodeMinted(h / 4u % (MintSpace / 4u), (int)(h % 4u));
		}

		// Packs a base-36 value < 36^3 into three of the four ASCII bytes, with '_' in `underscoreAt`.
		public static uint EncodeMinted(uint n, int underscoreAt)
		{
			var value = 0u;
			for (var i = 0; i < 4; i++)
			{
				if (i == underscoreAt)
				{
					value |= (uint)'_' << (8 * i);
					continue;
				}

				value |= (uint)Alphabet[(int)(n % 36)] << (8 * i);
				n /= 36;
			}

			return value;
		}

		// Packs a base-36 value < 36^4 into the four ASCII bytes the wire carries, most significant digit first.
		public static uint Encode(uint n)
		{
			var value = 0u;
			for (var i = 0; i < 4; i++)
			{
				value |= (uint)Alphabet[(int)(n % 36)] << (8 * i);
				n /= 36;
			}

			return value;
		}

		// True when every byte is one the client's reader accepts. '_' is accepted by the client and is what a
		// minted token carries, so this is the validity test, not the "did we mint it" test (`IsMinted`).
		public static bool IsWellFormed(uint hash)
		{
			for (var i = 0; i < 4; i++)
			{
				var c = (char)((hash >> (8 * i)) & 0xFF);
				if (!(c is >= '0' and <= '9' or >= 'A' and <= 'Z' || c == '_'))
					return false;
			}

			return true;
		}

		// The token as text, for logs, reports and test failure messages ("60GP", not "909133648").
		public static string Token(uint hash)
		{
			return new string([(char)(hash >> 24), (char)((hash >> 16) & 0xFF), (char)((hash >> 8) & 0xFF), (char)(hash & 0xFF)]);
		}
	}
}
