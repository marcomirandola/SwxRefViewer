using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SwxRefViewer
{
    public class OptionsForm : Form
    {
        readonly TextBox licenseBox;
        readonly CheckBox rawScanBox;
        readonly TextBox foldersBox;
        readonly Settings settings;

        public OptionsForm(Settings settings)
        {
            this.settings = settings;
            Text = "Options";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(560, 400);
            Font = SystemFonts.MessageBoxFont;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "SOLIDWORKS Document Manager license key (from the SOLIDWORKS customer portal).\r\nWithout a key the files are read with a raw scan, which is less exact."
            });
            licenseBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = settings.LicenseKey, ScrollBars = ScrollBars.Vertical };
            layout.Controls.Add(licenseBox);
            rawScanBox = new CheckBox { AutoSize = true, Text = "Always use the raw file scan (ignore the Document Manager)", Checked = settings.ForceRawScan };
            layout.Controls.Add(rawScanBox);
            layout.Controls.Add(new Label
            {
                AutoSize = true,
                Padding = new Padding(0, 8, 0, 0),
                Text = "Search folders, one per line (like 'Referenced Documents' in the SOLIDWORKS file locations):"
            });
            foldersBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = string.Join("\r\n", settings.SearchFolders)
            };
            layout.Controls.Add(foldersBox);
            var addFolder = new Button { Text = "Add folder...", AutoSize = true };
            addFolder.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    foldersBox.AppendText((foldersBox.TextLength > 0 ? "\r\n" : "") + dlg.SelectedPath);
                }
            };
            layout.Controls.Add(addFolder);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            ok.Click += (s, e) => Apply();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(layout);
        }

        void Apply()
        {
            settings.LicenseKey = licenseBox.Text.Trim();
            settings.ForceRawScan = rawScanBox.Checked;
            settings.SearchFolders = foldersBox.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            settings.Save();
        }
    }
}
