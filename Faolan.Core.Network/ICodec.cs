namespace Faolan.Core.Network
{
    // The common ground for the self-serializing wire types: an instance writes itself to a stream (Write) or a fresh
    // buffer (ToArray), and a static factory reads one back (Read). Read/Write are a matched pair - Read(stream over
    // x.ToArray()) == x. ToArray is a required member (not a default) so it is callable through a concrete reference.
    public interface ICodec<out T>
    {
        void Write(FaolanStream s);
        byte[] ToArray();
        static abstract T Read(FaolanStream stream);
    }
}
