using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace CLDLNA{
	internal partial class Form1 : Form{
		private readonly string foldersFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "folders.txt");
		private DlnaServer server;
		internal Form1(){InitializeComponent();}
		protected override void OnLoad(EventArgs e){
			base.OnLoad(e);
			LoadFolders();
			server = new DlnaServer("CLDLNA", 8200);
			RefreshServerFolders();
			server.Start();
			Text = "CLDLNA - " + server.BaseUrl;
		}
		protected override void OnFormClosing(FormClosingEventArgs e){
			if(server != null) server.Dispose();
			base.OnFormClosing(e);
		}
		private void butAdd_Click(object sender, EventArgs e){
			if(folderBrowserDialog1.ShowDialog(this) != DialogResult.OK) return;
			var p = Path.GetFullPath(folderBrowserDialog1.SelectedPath);
			if(listViewFolders.Items.Cast<ListViewItem>().Any(x => string.Equals(x.SubItems[1].Text, p, StringComparison.OrdinalIgnoreCase))) return;
			listViewFolders.Items.Add(new ListViewItem(new[]{ Path.GetFileName(p), p }));
			SaveFolders();
			RefreshServerFolders();
		}
		private void butRemove_Click(object sender, EventArgs e){
			listViewFolders.SelectedItems.Cast<ListViewItem>().ToList().ForEach(item => listViewFolders.Items.Remove(item));
			SaveFolders();
			RefreshServerFolders();
		}
		private void RefreshServerFolders(){
			if(server == null) return;
			server.SetFolders(listViewFolders.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text));
		}
		private void LoadFolders(){
			if(!File.Exists(foldersFile)) return;
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach(var line in File.ReadAllLines(foldersFile)){
				var p = (line ?? string.Empty).Trim();
				if(string.IsNullOrWhiteSpace(p) || !Directory.Exists(p) || !seen.Add(p)) continue;
				listViewFolders.Items.Add(new ListViewItem(new[]{ Path.GetFileName(p), p }));
			}
		}
		private void SaveFolders(){
			var folders = listViewFolders.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			File.WriteAllLines(foldersFile, folders);
		}
	}
}