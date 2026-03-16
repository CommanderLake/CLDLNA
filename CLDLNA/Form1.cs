using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CLDLNA {
	internal partial class Form1 : Form {
		private MiniDlnaServer server;
		internal Form1() { InitializeComponent(); }
		protected override void OnLoad(EventArgs e) {
			base.OnLoad(e);
			server = new MiniDlnaServer("CLDLNA", 8200);
			RefreshServerFolders();
			server.Start();
			Text = "CLDLNA - " + server.BaseUrl;
		}
		protected override void OnFormClosing(FormClosingEventArgs e) {
			if(server != null) server.Dispose();
			base.OnFormClosing(e);
		}
		private void butAdd_Click(object sender, EventArgs e) {
			if(folderBrowserDialog1.ShowDialog(this) != DialogResult.OK) return;
			var p = Path.GetFullPath(folderBrowserDialog1.SelectedPath);
			if(listViewFolders.Items.Cast<ListViewItem>().Any(x => string.Equals(x.SubItems[1].Text, p, StringComparison.OrdinalIgnoreCase))) return;
			listViewFolders.Items.Add(new ListViewItem(new[] { Path.GetFileName(p), p }));
			RefreshServerFolders();
		}
		private void butRemove_Click(object sender, EventArgs e) {
			listViewFolders.SelectedItems.Cast<ListViewItem>().ToList().ForEach(item => listViewFolders.Items.Remove(item));
			RefreshServerFolders();
		}
		private void RefreshServerFolders() {
			if(server == null) return;
			server.SetFolders(listViewFolders.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text));
		}
	}
}