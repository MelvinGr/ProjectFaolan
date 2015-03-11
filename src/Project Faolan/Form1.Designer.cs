namespace ProjectFaolan
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        void InitializeComponent()
        {
            this.richTextBox = new System.Windows.Forms.RichTextBox();
            this.databaseConnectButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // richTextBox
            // 
            this.richTextBox.Location = new System.Drawing.Point(12, 12);
            this.richTextBox.Name = "richTextBox";
            this.richTextBox.ReadOnly = true;
            this.richTextBox.Size = new System.Drawing.Size(660, 358);
            this.richTextBox.TabIndex = 0;
            this.richTextBox.Text = "";
            // 
            // databaseConnectButton
            // 
            this.databaseConnectButton.Location = new System.Drawing.Point(12, 376);
            this.databaseConnectButton.Name = "databaseConnectButton";
            this.databaseConnectButton.Size = new System.Drawing.Size(92, 23);
            this.databaseConnectButton.TabIndex = 1;
            this.databaseConnectButton.Text = "Start";
            this.databaseConnectButton.UseVisualStyleBackColor = true;
            this.databaseConnectButton.Click += new System.EventHandler(this.databaseConnectButton_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(684, 411);
            this.Controls.Add(this.databaseConnectButton);
            this.Controls.Add(this.richTextBox);
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(700, 450);
            this.MinimumSize = new System.Drawing.Size(700, 450);
            this.Name = "Form1";
            this.Text = "Project Faolan";
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.RichTextBox richTextBox;
        private System.Windows.Forms.Button databaseConnectButton;
    }
}

