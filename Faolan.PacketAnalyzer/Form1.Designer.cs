
namespace Faolan.PacketAnalyzer
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.inputTextBox = new System.Windows.Forms.TextBox();
            this.outputTextBox = new System.Windows.Forms.RichTextBox();
            this.outputListBox = new System.Windows.Forms.ListBox();
            this.parseButton = new System.Windows.Forms.Button();
            this.outputHexTextBox = new System.Windows.Forms.RichTextBox();
            this.SuspendLayout();
            // 
            // inputTextBox
            // 
            this.inputTextBox.Location = new System.Drawing.Point(12, 12);
            this.inputTextBox.Multiline = true;
            this.inputTextBox.Name = "inputTextBox";
            this.inputTextBox.Size = new System.Drawing.Size(176, 680);
            this.inputTextBox.TabIndex = 0;
            // 
            // outputTextBox
            // 
            this.outputTextBox.Location = new System.Drawing.Point(194, 412);
            this.outputTextBox.Name = "outputTextBox";
            this.outputTextBox.ReadOnly = true;
            this.outputTextBox.Size = new System.Drawing.Size(641, 309);
            this.outputTextBox.TabIndex = 3;
            this.outputTextBox.Text = "";
            // 
            // outputListBox
            // 
            this.outputListBox.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
            this.outputListBox.FormattingEnabled = true;
            this.outputListBox.HorizontalScrollbar = true;
            this.outputListBox.ItemHeight = 15;
            this.outputListBox.Location = new System.Drawing.Point(194, 12);
            this.outputListBox.Name = "outputListBox";
            this.outputListBox.Size = new System.Drawing.Size(1178, 394);
            this.outputListBox.TabIndex = 1;
            this.outputListBox.DrawItem += new System.Windows.Forms.DrawItemEventHandler(this.outputListBox_DrawItem);
            this.outputListBox.SelectedIndexChanged += new System.EventHandler(this.outputListBox_SelectedIndexChanged);
            // 
            // parseButton
            // 
            this.parseButton.Location = new System.Drawing.Point(12, 698);
            this.parseButton.Name = "parseButton";
            this.parseButton.Size = new System.Drawing.Size(176, 23);
            this.parseButton.TabIndex = 2;
            this.parseButton.Text = "Parse";
            this.parseButton.UseVisualStyleBackColor = true;
            this.parseButton.Click += new System.EventHandler(this.parseButton_Click);
            // 
            // outputHexTextBox
            // 
            this.outputHexTextBox.Location = new System.Drawing.Point(841, 412);
            this.outputHexTextBox.Name = "outputHexTextBox";
            this.outputHexTextBox.ReadOnly = true;
            this.outputHexTextBox.Size = new System.Drawing.Size(531, 309);
            this.outputHexTextBox.TabIndex = 4;
            this.outputHexTextBox.Text = "";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1384, 733);
            this.Controls.Add(this.outputHexTextBox);
            this.Controls.Add(this.parseButton);
            this.Controls.Add(this.outputListBox);
            this.Controls.Add(this.outputTextBox);
            this.Controls.Add(this.inputTextBox);
            this.Name = "Form1";
            this.Text = "Form1";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox inputTextBox;
        private System.Windows.Forms.RichTextBox outputTextBox;
        private System.Windows.Forms.ListBox outputListBox;
        private System.Windows.Forms.Button parseButton;
        private System.Windows.Forms.RichTextBox outputHexTextBox;
    }
}

