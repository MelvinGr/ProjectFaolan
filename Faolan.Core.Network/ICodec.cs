namespace Faolan.Core.Network
{
    public interface ICodec<out T>
    {
        void Write(FaolanStream s);
        static abstract T Read(FaolanStream stream);
    }
}
