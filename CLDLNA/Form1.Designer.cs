namespace CLDLNA {
	internal partial class Form1 {
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.IContainer components = null;

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing) {
			if(disposing && (components != null)) {
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent() {
			this.listViewFolders = new System.Windows.Forms.ListView();
			this.columnHeader1 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
			this.columnHeader2 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
			this.panel1 = new System.Windows.Forms.Panel();
			this.butRemove = new System.Windows.Forms.Button();
			this.butAdd = new System.Windows.Forms.Button();
			this.folderBrowserDialog1 = new System.Windows.Forms.FolderBrowserDialog();
			this.panel1.SuspendLayout();
			this.SuspendLayout();
			// 
			// listViewFolders
			// 
			this.listViewFolders.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
			this.listViewFolders.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeader1,
            this.columnHeader2});
			this.listViewFolders.GridLines = true;
			this.listViewFolders.HideSelection = false;
			this.listViewFolders.Location = new System.Drawing.Point(0, 0);
			this.listViewFolders.Margin = new System.Windows.Forms.Padding(0);
			this.listViewFolders.Name = "listViewFolders";
			this.listViewFolders.Size = new System.Drawing.Size(549, 427);
			this.listViewFolders.TabIndex = 0;
			this.listViewFolders.UseCompatibleStateImageBehavior = false;
			this.listViewFolders.View = System.Windows.Forms.View.Details;
			// 
			// columnHeader1
			// 
			this.columnHeader1.Text = "Folder Name";
			this.columnHeader1.Width = 100;
			// 
			// columnHeader2
			// 
			this.columnHeader2.Text = "Full Path";
			this.columnHeader2.Width = 444;
			// 
			// panel1
			// 
			this.panel1.Controls.Add(this.butRemove);
			this.panel1.Controls.Add(this.butAdd);
			this.panel1.Controls.Add(this.listViewFolders);
			this.panel1.Dock = System.Windows.Forms.DockStyle.Fill;
			this.panel1.Location = new System.Drawing.Point(0, 0);
			this.panel1.Margin = new System.Windows.Forms.Padding(0);
			this.panel1.Name = "panel1";
			this.panel1.Size = new System.Drawing.Size(549, 450);
			this.panel1.TabIndex = 1;
			// 
			// butRemove
			// 
			this.butRemove.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
			this.butRemove.Location = new System.Drawing.Point(75, 427);
			this.butRemove.Margin = new System.Windows.Forms.Padding(0);
			this.butRemove.Name = "butRemove";
			this.butRemove.Size = new System.Drawing.Size(75, 23);
			this.butRemove.TabIndex = 2;
			this.butRemove.Text = "Remove";
			this.butRemove.UseVisualStyleBackColor = true;
			this.butRemove.Click += new System.EventHandler(this.butRemove_Click);
			// 
			// butAdd
			// 
			this.butAdd.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
			this.butAdd.Location = new System.Drawing.Point(0, 427);
			this.butAdd.Margin = new System.Windows.Forms.Padding(0);
			this.butAdd.Name = "butAdd";
			this.butAdd.Size = new System.Drawing.Size(75, 23);
			this.butAdd.TabIndex = 1;
			this.butAdd.Text = "Add Folder";
			this.butAdd.UseVisualStyleBackColor = true;
			this.butAdd.Click += new System.EventHandler(this.butAdd_Click);
			// 
			// Form1
			// 
			this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.ClientSize = new System.Drawing.Size(549, 450);
			this.Controls.Add(this.panel1);
			this.Name = "Form1";
			this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
			this.Text = "CLDLNA";
			this.panel1.ResumeLayout(false);
			this.ResumeLayout(false);

		}

		#endregion

		private System.Windows.Forms.ListView listViewFolders;
		private System.Windows.Forms.ColumnHeader columnHeader1;
		private System.Windows.Forms.ColumnHeader columnHeader2;
		private System.Windows.Forms.Panel panel1;
		private System.Windows.Forms.Button butRemove;
		private System.Windows.Forms.Button butAdd;
		private System.Windows.Forms.FolderBrowserDialog folderBrowserDialog1;
	}
}

