using System;

namespace Faolan.Core.Protobuf
{
	public partial class ProtoDecryptionKey : ICloneable
	{
		public object Clone()
		{
			return new ProtoDecryptionKey { Key1 = Key1, Key2 = Key2, Key3 = Key3, Key4 = Key4 };
		}
	}
}
