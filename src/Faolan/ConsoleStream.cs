//  Console Widget - A handy library to route the Console and Debug output of .NET to your GUI
//  Written by Tebjan Halm 2010, tebjan()gmx.de
//
//  This library is free software; you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation; either
//  version 3 of the License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General
//  Public License along with this library.

using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ProjectFaolan
{
    public class ConsoleStream : TextWriter
    {
        //create writer with a texbox
        public ConsoleStream(RichTextBox box)
        {
            TextBox = box;
        }

        //the textbox we write into
        public RichTextBox TextBox { get; set; }
        //return default encoding
        public override Encoding Encoding => Encoding.Default;
        // This method is passed in to the SetTextCallBack delegate
        // to update the Text property of textBox.
        private void AppendText(string text)
        {
            TextBox.AppendText(text);
            TextBox.ScrollToCaret();
        }

        //write a string
        public override void Write(string value)
        {
            //only one thread can write in the textbox at a time
            lock (TextBox)
            {
                // Check if this method is running on a different thread
                // than the thread that created the control.
                if (TextBox.InvokeRequired)
                {
                    // It's on a different thread, so use Invoke.
                    //SetTextCallback d = AppendText;
                    //write async into the textbox and return
                    TextBox.BeginInvoke((SetTextCallback) AppendText, value);
                }
                else
                {
                    AppendText(value);
                }
            }
        }

        //write a string + new line
        public override void WriteLine(string value)
        {
            Write(value + Environment.NewLine);
        }

        //overwrite all other write methods
        public override void Write(bool value) => Write(value.ToString());
        public override void Write(char value) => Write(value.ToString());
        public override void Write(char[] buffer) => Write(new string(buffer));
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
        public override void Write(decimal value) => Write(value.ToString());
        public override void Write(double value) => Write(value.ToString());
        public override void Write(float value) => Write(value.ToString());
        public override void Write(int value) => Write(value.ToString());
        public override void Write(long value) => Write(value.ToString());
        public override void Write(string format, object arg0) => WriteLine(format, arg0);
        public override void Write(string format, object arg0, object arg1) => WriteLine(format, arg0, arg1);

        public override void Write(string format, object arg0, object arg1, object arg2)
            => WriteLine(format, arg0, arg1, arg2);

        public override void Write(string format, params object[] arg) => WriteLine(format, arg);
        public override void Write(uint value) => WriteLine(value.ToString());
        public override void Write(ulong value) => WriteLine(value.ToString());
        public override void Write(object value) => WriteLine(value.ToString());
        public override void WriteLine() => Write(Environment.NewLine);
        public override void WriteLine(bool value) => WriteLine(value.ToString());
        public override void WriteLine(char value) => WriteLine(value.ToString());
        public override void WriteLine(char[] buffer) => WriteLine(new string(buffer));

        public override void WriteLine(char[] buffer, int index, int count)
            => WriteLine(new string(buffer, index, count));

        public override void WriteLine(decimal value) => WriteLine(value.ToString());
        public override void WriteLine(double value) => WriteLine(value.ToString());
        public override void WriteLine(float value) => WriteLine(value.ToString());
        public override void WriteLine(int value) => WriteLine(value.ToString());
        public override void WriteLine(long value) => WriteLine(value.ToString());
        public override void WriteLine(string format, object arg0) => WriteLine(format, arg0);
        public override void WriteLine(string format, object arg0, object arg1) => WriteLine(format, arg0, arg1);

        public override void WriteLine(string format, object arg0, object arg1, object arg2)
            => WriteLine(format, arg0, arg1, arg2);

        public override void WriteLine(string format, params object[] arg) => WriteLine(format, arg);
        public override void WriteLine(uint value) => WriteLine(value.ToString());
        public override void WriteLine(ulong value) => WriteLine(value.ToString());
        public override void WriteLine(object value) => WriteLine(value.ToString());
        //the delegate we pass to the textbox
        private delegate void SetTextCallback(string text);
    }
}