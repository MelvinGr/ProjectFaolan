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
        private void InitializeComponent()
        {
            this.richTextBox = new System.Windows.Forms.RichTextBox();
            this.databaseConnectButton = new System.Windows.Forms.Button();
            this.universeAgentButton = new System.Windows.Forms.Button();
            this.playerAgentButton = new System.Windows.Forms.Button();
            this.csPlayerAgentButton = new System.Windows.Forms.Button();
            this.agentServerButton = new System.Windows.Forms.Button();
            this.gameServerButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // richTextBox
            // 
            this.richTextBox.Location = new System.Drawing.Point(12, 12);
            this.richTextBox.Name = "richTextBox";
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
            this.databaseConnectButton.Text = "Database";
            this.databaseConnectButton.UseVisualStyleBackColor = true;
            this.databaseConnectButton.Click += new System.EventHandler(this.databaseConnectButton_Click);
            // 
            // universeAgentButton
            // 
            this.universeAgentButton.Location = new System.Drawing.Point(110, 376);
            this.universeAgentButton.Name = "universeAgentButton";
            this.universeAgentButton.Size = new System.Drawing.Size(92, 23);
            this.universeAgentButton.TabIndex = 2;
            this.universeAgentButton.Text = "UniverseAgent";
            this.universeAgentButton.UseVisualStyleBackColor = true;
            this.universeAgentButton.Click += new System.EventHandler(this.universeAgentButton_Click);
            // 
            // playerAgentButton
            // 
            this.playerAgentButton.Location = new System.Drawing.Point(208, 376);
            this.playerAgentButton.Name = "playerAgentButton";
            this.playerAgentButton.Size = new System.Drawing.Size(92, 23);
            this.playerAgentButton.TabIndex = 3;
            this.playerAgentButton.Text = "PlayerAgent";
            this.playerAgentButton.UseVisualStyleBackColor = true;
            this.playerAgentButton.Click += new System.EventHandler(this.playerAgentButton_Click);
            // 
            // csPlayerAgentButton
            // 
            this.csPlayerAgentButton.Location = new System.Drawing.Point(306, 376);
            this.csPlayerAgentButton.Name = "csPlayerAgentButton";
            this.csPlayerAgentButton.Size = new System.Drawing.Size(92, 23);
            this.csPlayerAgentButton.TabIndex = 4;
            this.csPlayerAgentButton.Text = "CSPlayerAgent";
            this.csPlayerAgentButton.UseVisualStyleBackColor = true;
            this.csPlayerAgentButton.Click += new System.EventHandler(this.csPlayerAgentButton_Click);
            // 
            // agentServerButton
            // 
            this.agentServerButton.Location = new System.Drawing.Point(404, 376);
            this.agentServerButton.Name = "agentServerButton";
            this.agentServerButton.Size = new System.Drawing.Size(92, 23);
            this.agentServerButton.TabIndex = 5;
            this.agentServerButton.Text = "AgentServer";
            this.agentServerButton.UseVisualStyleBackColor = true;
            this.agentServerButton.Click += new System.EventHandler(this.agentServerButton_Click);
            // 
            // gameServerButton
            // 
            this.gameServerButton.Location = new System.Drawing.Point(502, 376);
            this.gameServerButton.Name = "gameServerButton";
            this.gameServerButton.Size = new System.Drawing.Size(92, 23);
            this.gameServerButton.TabIndex = 6;
            this.gameServerButton.Text = "GameServer";
            this.gameServerButton.UseVisualStyleBackColor = true;
            this.gameServerButton.Click += new System.EventHandler(this.gameServerButton_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(684, 411);
            this.Controls.Add(this.gameServerButton);
            this.Controls.Add(this.agentServerButton);
            this.Controls.Add(this.csPlayerAgentButton);
            this.Controls.Add(this.playerAgentButton);
            this.Controls.Add(this.universeAgentButton);
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
        private System.Windows.Forms.Button universeAgentButton;
        private System.Windows.Forms.Button playerAgentButton;
        private System.Windows.Forms.Button csPlayerAgentButton;
        private System.Windows.Forms.Button agentServerButton;
        private System.Windows.Forms.Button gameServerButton;
    }
}

