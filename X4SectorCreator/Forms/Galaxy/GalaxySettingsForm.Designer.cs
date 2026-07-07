namespace X4SectorCreator.Forms
{
    partial class GalaxySettingsForm
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
            label1 = new Label();
            chkCustomGalaxy = new CheckBox();
            txtGalaxyName = new TextBox();
            BtnSave = new Button();
            BtnCancel = new Button();
            chkDisableAllStorylines = new CheckBox();
            BtnGenerateProceduralGalaxy = new Button();
            BtnSaveAndClose = new Button();
            label2 = new Label();
            CmbPlayerHq = new ComboBox();
            BtnSetFactionRelations = new Button();
            BtnShuffleGalaxy = new Button();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Segoe UI", 12F);
            label1.Location = new Point(14, 56);
            label1.Name = "label1";
            label1.Size = new Size(131, 28);
            label1.TabIndex = 0;
            label1.Text = "Galaxy Name:";
            // 
            // chkCustomGalaxy
            // 
            chkCustomGalaxy.AutoSize = true;
            chkCustomGalaxy.Location = new Point(291, 16);
            chkCustomGalaxy.Margin = new Padding(3, 4, 3, 4);
            chkCustomGalaxy.Name = "chkCustomGalaxy";
            chkCustomGalaxy.Size = new Size(129, 24);
            chkCustomGalaxy.TabIndex = 1;
            chkCustomGalaxy.Text = "Custom Galaxy";
            chkCustomGalaxy.UseVisualStyleBackColor = true;
            chkCustomGalaxy.CheckedChanged += ChkCustomGalaxy_CheckedChanged;
            // 
            // txtGalaxyName
            // 
            txtGalaxyName.Enabled = false;
            txtGalaxyName.Location = new Point(136, 56);
            txtGalaxyName.Margin = new Padding(3, 4, 3, 4);
            txtGalaxyName.Name = "txtGalaxyName";
            txtGalaxyName.Size = new Size(275, 27);
            txtGalaxyName.TabIndex = 2;
            txtGalaxyName.Text = "xu_ep2_universe";
            txtGalaxyName.TextChanged += txtGalaxyName_TextChanged;
            txtGalaxyName.KeyPress += TxtGalaxyName_KeyPress;
            // 
            // BtnSave
            // 
            BtnSave.Location = new Point(101, 181);
            BtnSave.Margin = new Padding(3, 4, 3, 4);
            BtnSave.Name = "BtnSave";
            BtnSave.Size = new Size(101, 44);
            BtnSave.TabIndex = 3;
            BtnSave.Text = "Save";
            BtnSave.UseVisualStyleBackColor = true;
            BtnSave.Click += BtnSave_Click;
            // 
            // BtnCancel
            // 
            BtnCancel.Location = new Point(13, 183);
            BtnCancel.Margin = new Padding(3, 4, 3, 4);
            BtnCancel.Name = "BtnCancel";
            BtnCancel.Size = new Size(81, 44);
            BtnCancel.TabIndex = 4;
            BtnCancel.Text = "Cancel";
            BtnCancel.UseVisualStyleBackColor = true;
            BtnCancel.Click += BtnCancel_Click;
            // 
            // chkDisableAllStorylines
            // 
            chkDisableAllStorylines.AutoSize = true;
            chkDisableAllStorylines.Location = new Point(14, 16);
            chkDisableAllStorylines.Margin = new Padding(3, 4, 3, 4);
            chkDisableAllStorylines.Name = "chkDisableAllStorylines";
            chkDisableAllStorylines.Size = new Size(167, 24);
            chkDisableAllStorylines.TabIndex = 5;
            chkDisableAllStorylines.Text = "Disable all storylines";
            chkDisableAllStorylines.UseVisualStyleBackColor = true;
            chkDisableAllStorylines.CheckedChanged += chkDisableAllStorylines_CheckedChanged;
            // 
            // BtnGenerateProceduralGalaxy
            // 
            BtnGenerateProceduralGalaxy.Location = new Point(13, 231);
            BtnGenerateProceduralGalaxy.Margin = new Padding(3, 4, 3, 4);
            BtnGenerateProceduralGalaxy.Name = "BtnGenerateProceduralGalaxy";
            BtnGenerateProceduralGalaxy.Size = new Size(398, 44);
            BtnGenerateProceduralGalaxy.TabIndex = 9;
            BtnGenerateProceduralGalaxy.Text = "Generate Procedural Galaxy";
            BtnGenerateProceduralGalaxy.UseVisualStyleBackColor = true;
            BtnGenerateProceduralGalaxy.Click += BtnGenerateProceduralGalaxy_Click;
            // 
            // BtnSaveAndClose
            // 
            BtnSaveAndClose.Location = new Point(208, 183);
            BtnSaveAndClose.Margin = new Padding(3, 4, 3, 4);
            BtnSaveAndClose.Name = "BtnSaveAndClose";
            BtnSaveAndClose.Size = new Size(202, 44);
            BtnSaveAndClose.TabIndex = 10;
            BtnSaveAndClose.Text = "Save and Close";
            BtnSaveAndClose.UseVisualStyleBackColor = true;
            BtnSaveAndClose.Click += BtnSaveAndClose_Click;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 12F);
            label2.Location = new Point(27, 95);
            label2.Name = "label2";
            label2.Size = new Size(117, 28);
            label2.TabIndex = 11;
            label2.Text = "PHQ Sector:";
            // 
            // CmbPlayerHq
            // 
            CmbPlayerHq.FormattingEnabled = true;
            CmbPlayerHq.Location = new Point(136, 95);
            CmbPlayerHq.Margin = new Padding(3, 4, 3, 4);
            CmbPlayerHq.Name = "CmbPlayerHq";
            CmbPlayerHq.Size = new Size(275, 28);
            CmbPlayerHq.TabIndex = 12;
            CmbPlayerHq.SelectedIndexChanged += CmbPlayerHq_SelectedIndexChanged;
            // 
            // BtnSetFactionRelations
            // 
            BtnSetFactionRelations.Location = new Point(14, 135);
            BtnSetFactionRelations.Margin = new Padding(3, 4, 3, 4);
            BtnSetFactionRelations.Name = "BtnSetFactionRelations";
            BtnSetFactionRelations.Size = new Size(398, 44);
            BtnSetFactionRelations.TabIndex = 13;
            BtnSetFactionRelations.Text = "Set Faction Relations";
            BtnSetFactionRelations.UseVisualStyleBackColor = true;
            BtnSetFactionRelations.Click += BtnSetFactionRelations_Click;
            // 
            // BtnShuffleGalaxy
            // 
            BtnShuffleGalaxy.Location = new Point(12, 279);
            BtnShuffleGalaxy.Margin = new Padding(3, 4, 3, 4);
            BtnShuffleGalaxy.Name = "BtnShuffleGalaxy";
            BtnShuffleGalaxy.Size = new Size(398, 44);
            BtnShuffleGalaxy.TabIndex = 14;
            BtnShuffleGalaxy.Text = "Shuffle Galaxy";
            BtnShuffleGalaxy.UseVisualStyleBackColor = true;
            BtnShuffleGalaxy.Click += BtnShuffleGalaxy_Click;
            // 
            // GalaxySettingsForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(418, 332);
            Controls.Add(BtnShuffleGalaxy);
            Controls.Add(BtnSetFactionRelations);
            Controls.Add(CmbPlayerHq);
            Controls.Add(label2);
            Controls.Add(BtnSaveAndClose);
            Controls.Add(BtnGenerateProceduralGalaxy);
            Controls.Add(chkDisableAllStorylines);
            Controls.Add(BtnCancel);
            Controls.Add(BtnSave);
            Controls.Add(txtGalaxyName);
            Controls.Add(chkCustomGalaxy);
            Controls.Add(label1);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Margin = new Padding(3, 4, 3, 4);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "GalaxySettingsForm";
            Text = "Galaxy Settings";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
        private CheckBox chkCustomGalaxy;
        private TextBox txtGalaxyName;
        private Button BtnSave;
        private Button BtnCancel;
        private CheckBox chkDisableAllStorylines;
        private Button BtnGenerateProceduralGalaxy;
        private Button BtnSaveAndClose;
        private Label label2;
        private ComboBox CmbPlayerHq;
        private Button BtnSetFactionRelations;
        private Button BtnShuffleGalaxy;
    }
}