using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Faolan.PacketAnalyzer
{
    public partial class Form1 : Form
    {
        private static readonly Regex Regex1 = new(@"[^\w]*");

        public Form1()
        {
            InitializeComponent();

            outputListBox.DrawMode = DrawMode.Normal;
        }

        private void parseButton_Click(object sender, EventArgs e)
        {
            outputTextBox.Text = "";
            outputListBox.Items.Clear();

            inputTextBox.Text = Regex1.Replace(inputTextBox.Text.Replace("0x", ""), "");
            var bytes = inputTextBox.Text.HexToByteArray();

            if (bytes == null || bytes.Length < 10)
            {
                Error("Invalid input");
                return;
            }

            if (bytes[0] == 0x80 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x05)
                DumpCompressed(bytes);
            else
                Dump(bytes);
        }

        private void outputListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (outputListBox.SelectedIndex == -1)
            {
                outputTextBox.Text = "";
                return;
            }

            var packetWrapper = (PacketWrapper) outputListBox.Items[e.Index];
            var text = packetWrapper.ToString();

            e.Graphics.DrawString(text, e.Font, new SolidBrush(Color.Blue), e.Bounds);

            var width = (int) e.Graphics.MeasureString(text, e.Font).Width;
            if (width > outputListBox.HorizontalExtent)
                outputListBox.HorizontalExtent = width;
        }

        private void outputListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            outputListBox.Invalidate();
            if (outputListBox.SelectedIndex == -1)
            {
                outputTextBox.Text = "";
                return;
            }

            var packetWrapper = (PacketWrapper) outputListBox.Items[outputListBox.SelectedIndex];
            outputTextBox.Text = PacketUtils.PacketToCsCode(new ConanPacket(packetWrapper.Data), packetWrapper.Index);

            outputHexTextBox.Text = packetWrapper.Data?.HexDump();
        }

        private void Dump(byte[] bytes, bool reverse = true)
        {
            var stream = new MemoryStream(bytes);
            var i = 0;
            while (true)
                try
                {
                    var packetLengthBuffer = new byte[4];
                    stream.Read(packetLengthBuffer, 0, 4);
                    var length = reverse
                        ? BitConverter.ToInt32(packetLengthBuffer.Reverse().ToArray(), 0)
                        : BitConverter.ToInt32(packetLengthBuffer.ToArray(), 0);

                    var packetBuffer = new byte[length];
                    stream.Read(packetBuffer, 0, length);

                    var completePacket = packetLengthBuffer.Concat(packetBuffer).ToHexString();
                    outputListBox.Items.Add(new PacketWrapper(i, bytes, completePacket));

                    i++;
                    if (stream.Position == stream.Length)
                        break;
                }
                catch (Exception e)
                {
                    Error(e.Message);
                    break;
                }
        }

        private void DumpCompressed(byte[] b)
        {
            var bytes = UnCompress(b.Skip(9).ToArray()).ToArray();

            var stream = new MemoryStream(bytes);
            var i = 0;
            while (true)
                try
                {
                    var packetLengthBuffer = new byte[4];
                    stream.Read(packetLengthBuffer, 0, 4);
                    var length = BitConverter.ToInt32(packetLengthBuffer.Reverse().ToArray(), 0);

                    var packetBuffer = new byte[length];
                    stream.Read(packetBuffer, 0, length);

                    var completePacket = packetLengthBuffer.Concat(packetBuffer).ToArray().ToHexString();
                    outputListBox.Items.Add(new PacketWrapper(i, bytes, completePacket));

                    i++;
                    if (stream.Position == stream.Length)
                        break;
                }
                catch (Exception e)
                {
                    Error(e.Message);
                    break;
                }
        }

        private static byte[] UnCompress(byte[] input)
        {
            var decompressor = new Inflater();
            decompressor.SetInput(input);

            // Create an expandable byte array to hold the decompressed data  
            var bos = new MemoryStream(input.Length);

            var buf = new byte[10240];
            while (!decompressor.IsFinished && !decompressor.IsNeedingInput)
            {
                var count = decompressor.Inflate(buf);
                bos.Write(buf, 0, count);
            }

            // Get the decompressed data  
            return bos.ToArray();
        }

        private void Error(string txt)
        {
            outputTextBox.Text = txt;
        }

        private class PacketWrapper
        {
            public PacketWrapper(int index, byte[] data, string hex)
            {
                Index = index;
                Data = data;
                Hex = hex;
                ConanPacket = new ConanPacket(Data);
            }

            public int Index { get; }
            public byte[] Data { get; }
            public string Hex { get; }
            public ConanPacket ConanPacket { get; }

            public override string ToString()
            {
                return $"{Index}: {Hex}";
            }
        }
    }
}