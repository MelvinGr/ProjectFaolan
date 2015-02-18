using System;
using System.IO;

namespace LibFaolan
{
    public class Logger
    {
        public readonly string Tag;

        public Logger(string tag = null)
        {
            Tag = tag;
        }

        public TextWriter TextWriter { get; set; }

        public void Write(string text)
        {
            //lock (TextWriter)
            {
                if (TextWriter != null)
                    TextWriter.Write(Tag != null ? Tag + text : text);
                else
                    Console.Write(Tag != null ? Tag + text : text);
            }
        }

        public void WriteLine()
        {
            Write(Environment.NewLine);
        }

        public void WriteLine(string value)
        {
            Write(value + Environment.NewLine);
        }

        public void WriteLine(string format, params object[] arg)
        {
            WriteLine(string.Format(format, arg));
        }
    }
}