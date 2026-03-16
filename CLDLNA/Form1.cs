using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace CLDLNA{
	internal partial class Form1 : Form{
		internal Form1(){InitializeComponent();}
		private void butAdd_Click(object sender, System.EventArgs e) {
			if(folderBrowserDialog1.ShowDialog(this) != DialogResult.OK) return;
			listViewFolders.Items.Add(new ListViewItem(new []{Path.GetFileName(folderBrowserDialog1.SelectedPath), folderBrowserDialog1.SelectedPath}));
		}
		private void butRemove_Click(object sender, System.EventArgs e) {
			listViewFolders.SelectedItems.Cast<ListViewItem>().ToList().ForEach(item => listViewFolders.Items.Remove(item));
		}
	}
}