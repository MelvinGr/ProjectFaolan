using System;

namespace Faolan.Core.Protobuf
{
	// A 128-bit TEA key (the wire's four words). Hand-extended over the generated protobuf partial.
	//
	// It is a VALUE, so it compares by value. The generated class is a plain reference type, which means
	// `Assert.Equal(a, b)` on two identical keys fails and `key1 == key2` silently asks the wrong question - a
	// footgun for exactly the comparisons this type invites (does this item's key match the template's? did the
	// re-encoded LDB key survive the round-trip?). Equality here is the four words and nothing else.
	public partial class ProtoDecryptionKey : ICloneable, IEquatable<ProtoDecryptionKey>
	{
		public object Clone()
		{
			return new ProtoDecryptionKey
			{
				Key1 = Key1,
				Key2 = Key2,
				Key3 = Key3,
				Key4 = Key4
			};
		}

		public bool Equals(ProtoDecryptionKey? other)
		{
			return other != null && (ReferenceEquals(this, other) || (Key1 == other.Key1 && Key2 == other.Key2 && Key3 == other.Key3 && Key4 == other.Key4));
		}

		public override bool Equals(object? obj)
		{
			return Equals(obj as ProtoDecryptionKey);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Key1, Key2, Key3, Key4);
		}

		public static bool operator ==(ProtoDecryptionKey? left, ProtoDecryptionKey? right)
		{
			return left?.Equals(right) ?? right is null;
		}

		public static bool operator !=(ProtoDecryptionKey? left, ProtoDecryptionKey? right)
		{
			return !(left == right);
		}

		// All four words zero - the "no key" a default-constructed instance carries, distinguishable from a real key
		// without reaching for null (the owned EF mapping cannot be null per-column).
		public bool IsEmpty => Key1 == 0 && Key2 == 0 && Key3 == 0 && Key4 == 0;

		public override string ToString()
		{
			return $"{Key1:X8}{Key2:X8}{Key3:X8}{Key4:X8}";
		}
	}
}
