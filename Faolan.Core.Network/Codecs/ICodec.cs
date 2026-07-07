namespace Faolan.Core.Network.Codecs
{
    // The base contract for anything that serializes itself onto the wire: it can write into an existing stream
    // (Write, for composing into a larger body) or produce a fresh buffer (ToArray). Write-only types - packet
    // sub-records that are built but never parsed back - implement just this.
    public interface IWireWritable
    {
        void Write(FaolanStream s);
        byte[] ToArray();
    }

    // A bidirectional wire type: IWireWritable plus a static factory that reads one back. Read/Write are a matched
    // pair - Read(stream over x.ToArray()) == x.
    public interface ICodec<out T> : IWireWritable
    {
        static abstract T Read(FaolanStream stream);
    }
}
