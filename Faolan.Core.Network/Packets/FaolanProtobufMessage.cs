using System.Text.Json;
using ProtoBuf;

namespace Faolan.Core.Network.Packets
{
	/// <summary>
	///     Base class for protobuf-net models that should preserve unknown fields
	///     during deserialization / reserialization.
	///     Inheriting from this class enables protobuf extension-data support via
	///     <see cref="IExtensible" /> so forward-compatible fields are retained even
	///     when the current model does not explicitly define them.
	/// </summary>
	[ProtoContract]
	public abstract class FaolanProtobufMessage : IExtensible
	{
		private IExtension? _extension;
		public IExtension? Extension => _extension;

		IExtension IExtensible.GetExtensionObject(bool createIfMissing)
		{
			return Extensible.GetExtensionObject(ref _extension, createIfMissing);
		}

		public override string ToString()
		{
			return JsonSerializer.Serialize(this, GetType(), new JsonSerializerOptions { WriteIndented = true });
		}
	}
}
