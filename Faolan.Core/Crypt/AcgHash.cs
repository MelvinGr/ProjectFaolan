namespace Faolan.Core.Crypt
{
	// The client's ACGHash: the 4-character token that identifies one item template on the wire
	// (ProtoAcgItem.hash, stored harvested as Items.Hash). A token namespace, the same family as the neighbours
	// in this folder - Bkdr31 turns a NAME into an id, Elf28/Crc32 a byte run into one; this one is the id space
	// the client's own authoring pipeline hands out.
	//
	// What it is, measured over the 3,993 harvested tokens and the 120 capture dumps (#723):
	//   * four bytes, every one of them [0-9A-Z] - the charset the client's ACGHash stream reader FUN_005d7190
	//     validates, and the only thing it checks: it compares the word against NO expected value (#547).
	//   * PER TEMPLATE and perfectly stable - 107,929 sightings across different accounts, characters and sessions
	//     produced 0 templates carrying two different tokens, and 0 carrying two identity quads.
	//   * carrying NO information about the item. Uniform at every position (chi2 34.9/39.0/42.4/42.4 against a
	//     49.8 critical value), no check digit, Spearman -0.015 against the template id, affine/LCG in the id
	//     rejected, and not any of crc32/adler32/fnv1a/bkdr31/djb2/md5/sha1 over the id/name/identity quad under
	//     either reduction (0/400 on all 84 combinations). It is an authoring-pipeline identity, not a
	//     computation - which is why nothing derives it, and #547 proved it resolves to nothing in 28 GB of RDB
	//     content either. So unlike every other hash in this folder, ACGHash has no algorithm to reproduce: the
	//     only operations that exist are "carry the harvested one" and "mint a fresh one".
	//
	// The client therefore has no oracle to validate a token against, and cannot tell a minted one from a retail
	// one. Confirmed in-client 2026-08-08: an item given an unseen in-charset token ('ZZZZ') and a previously
	// HASH-LESS item given a minted one ('ZZZY') both render correctly - on the live DispatchAddItem path AND,
	// after a relog, through the world-entry snapshot.
	//
	// ⚠️ A minted token MUST be in-charset. The obvious form of that experiment - "send a deliberately wrong
	// hash", i.e. a random u32 like 0xDEADBEEF - tests the CHARSET VALIDATOR, not the identity, and reads as
	// "the client validates the hash, minting is impossible". Mint through Mint() and nothing else.
	public static class AcgHash
	{
		// The token alphabet, in base-36 digit order. The client's reader also accepts '_', but no retail item
		// token uses one (0 of 4,615), so minting stays inside the proven set rather than relying on an untested
		// byte. That unused byte is also the structural fix for salt collisions - see Salt.
		private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

		// 36^4 = 1,679,616 distinct tokens.
		public const uint Space = 36u * 36u * 36u * 36u;

		// Re-rolls every minted token. A collision is not a correctness bug (the client resolves nothing through the
		// token) but two templates sharing an identity is a property retail maintains, so we maintain it too -
		// `--report acg-token-check` scores the shipped data and names the next clean salt.
		//
		// ⚠️ A bump is a LOTTERY, not a fix, and the numbers say so. With today's population (5,144 items: 4,615
		// harvested, 529 minted) the expected number of collisions per salt is
		//     529x4615/36^4 (minted vs harvested) + 529^2/(2x36^4) (minted vs minted) = 1.45 + 0.08 = 1.54,
		// so a given salt is clean with probability e^-1.54 ~ 22% - measured, not estimated: 90 of 400 consecutive
		// salts are clean. It WILL fire again, and the dominant term grows every time captures are harvested.
		//
		// The structural fix, when someone wants one: the client's reader accepts '_' and **zero of the 4,615
		// harvested tokens contain one**, so minting into the '_'-bearing part of the space is disjoint from every
		// harvested token BY CONSTRUCTION and kills the 1.45. It is not done here because '_' is the one byte the
		// in-client check never exercised ('ZZZZ' and 'ZZZY' were), and shipping it unverified would risk 529
		// currently-working items rendering as nothing. Gitea #899 carries it, gated on that check.
		private const uint Salt = 0x14D6F0B6;

		/// <summary>The salt in force, so `--report acg-token-check` can sweep forward from it for a clean one.</summary>
		public const uint CurrentSalt = Salt;

		// The token to put on the wire for a template: the HARVESTED one when a capture ever showed this template
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
		public static uint Mint(uint templateId)
		{
			return MintWith(templateId, Salt);
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

			return Encode(h % Space);
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

		// True when every byte is one the client's reader accepts. '_' is accepted by the client but never minted
		// here, so this is the validity test, not the "did we mint it" test.
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
