namespace Faolan.Core.Network
{
    // The common ground for the self-serializing wire types: an instance writes itself to a stream (Write), a static
    // factory reads one back (Read), and ToArray is the byte[] convenience (a default over Write, which the region
    // codecs already provide directly). Read/Write are a matched pair - Read(new FaolanStream(x.ToArray())) == x.
    public interface ICodec<out T>
    {
        void Write(FaolanStream s);
        static abstract T Read(FaolanStream stream);

        byte[] ToArray()
        {
            using var s = new FaolanStream();
            Write(s);
            return s.ToArray();
        }
    }
}
