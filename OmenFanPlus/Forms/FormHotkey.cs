using OmenFanPlus.Properties;
using OmenFanPlus.Services;
using System.Diagnostics;

namespace OmenFanPlus.Forms
{
    /// <summary>
    /// "Remap OMEN key to...": the key chord or program the OMEN key triggers instead of the popup.
    /// Changes are kept locally and written only on OK, so Cancel (or Esc) really cancels.
    /// </summary>
    public partial class FormHotkey : Form
    {
        private bool isListening = false;
        private Keys selectedKey;
        private bool _loading = true;

        public FormHotkey()
        {
            InitializeComponent();
            AcceptButton = OKButton;
            CancelButton = CancelBtn;

            // Real key codes only: no aliases, no mouse buttons, no modifier-only entries.
            var keys = Enum.GetValues<Keys>()
                .Where(k => k != Keys.None && ((int)k & 0xFFFF) == (int)k && (int)k > 0 &&
                            k is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu or
                                      Keys.LButton or Keys.RButton or Keys.MButton or Keys.XButton1 or Keys.XButton2))
                .Distinct()
                .ToList();
            keyListCombobox.DataSource = keys;

            selectedKey = Settings.Default.AlternateKey;
            enableRemappingCheckbox.Checked = Settings.Default.UseOmenAlternateKey;
            execProgramCheckbox.Checked = Settings.Default.UseOmenKeyExecute;
            fileExecPath.Text = Settings.Default.ExecutePath;
            fileExecArguments.Text = Settings.Default.ExecuteArguments;
            _loading = false;
            UpdateUI();
        }

        private void IdentifyButton_Click(object sender, EventArgs e)
        {
            isListening = !isListening;
            IdentifyButton.Text = isListening ? "Listening..." : "Identify";
        }

        private readonly Color activeColor = Color.FromArgb(153, 153, 153);
        private readonly Color inactiveColor = Color.FromArgb(64, 64, 64);
        private readonly Color activeTextColor = Color.FromArgb(31, 31, 31);
        private readonly Color inactiveTextColor = Color.FromArgb(230, 230, 230);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (isListening)
            {
                var noModifiersKey = keyData & Keys.KeyCode;
                if (noModifiersKey is Keys.ShiftKey or Keys.ControlKey or Keys.Menu)
                    return true;

                selectedKey = keyData;
                UpdateUI();
                IdentifyButton_Click(this, EventArgs.Empty);
                enableRemappingCheckbox.Checked = true;
                return true;    // indicate that you handled this keystroke
            }

            // Call the base class
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CancelBtn_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void OKButton_Click(object sender, EventArgs e)
        {
            if (enableRemappingCheckbox.Checked && (selectedKey & Keys.KeyCode) == Keys.None)
            {
                MessageBox.Show("Pick a key to send, or untick 'Remap key'.", "OmenFanPlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var path = fileExecPath.Text.Trim();
            if (execProgramCheckbox.Checked && !File.Exists(path))
            {
                MessageBox.Show("Pick an existing program to run, or untick 'Execute program'.", "OmenFanPlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Settings.Default.AlternateKey = selectedKey;
            Settings.Default.UseOmenAlternateKey = enableRemappingCheckbox.Checked;
            Settings.Default.UseOmenKeyExecute = execProgramCheckbox.Checked;
            Settings.Default.ExecutePath = path;
            Settings.Default.ExecuteArguments = fileExecArguments.Text;
            FanModeController.TrySave();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void enableRemappingCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (!_loading && enableRemappingCheckbox.Checked)
                execProgramCheckbox.Checked = false;
        }

        private void keyListCombobox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (keyListCombobox.SelectedItem is not Keys key)
                return;
            // Keep Ctrl/Alt/Shift that were toggled before the key was picked.
            selectedKey = (selectedKey & Keys.Modifiers) | key;
            UpdateUI();
        }

        private void UpdateUI()
        {
            var noModifiersKey = selectedKey & Keys.KeyCode;

            // Dont trigger event
            keyListCombobox.SelectedIndexChanged -= keyListCombobox_SelectedIndexChanged;
            keyListCombobox.SelectedItem = noModifiersKey;
            keyListCombobox.SelectedIndexChanged += keyListCombobox_SelectedIndexChanged;

            CtrlButton.BackColor = selectedKey.HasFlag(Keys.Control) ? activeColor : inactiveColor;
            AltBtn.BackColor = selectedKey.HasFlag(Keys.Alt) ? activeColor : inactiveColor;
            ShiftBtn.BackColor = selectedKey.HasFlag(Keys.Shift) ? activeColor : inactiveColor;
            CtrlButton.ForeColor = selectedKey.HasFlag(Keys.Control) ? activeTextColor : inactiveTextColor;
            AltBtn.ForeColor = selectedKey.HasFlag(Keys.Alt) ? activeTextColor : inactiveTextColor;
            ShiftBtn.ForeColor = selectedKey.HasFlag(Keys.Shift) ? activeTextColor : inactiveTextColor;
        }

        private void CtrlButton_Click(object sender, EventArgs e)
        {
            selectedKey ^= Keys.Control;
            UpdateUI();
        }

        private void AltBtn_Click(object sender, EventArgs e)
        {
            selectedKey ^= Keys.Alt;
            UpdateUI();
        }

        private void ShiftBtn_Click(object sender, EventArgs e)
        {
            selectedKey ^= Keys.Shift;
            UpdateUI();
        }

        private void execProgramCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (!_loading && execProgramCheckbox.Checked)
                enableRemappingCheckbox.Checked = false;
        }

        private void selectFileButton_Click(object sender, EventArgs e)
        {
            selectProgramDialog.ShowDialog();
        }

        private void selectProgramDialog_FileOk(object sender, System.ComponentModel.CancelEventArgs e)
        {
            fileExecPath.Text = selectProgramDialog.FileName;
        }

        private void fileExecPath_TextChanged(object sender, EventArgs e)
        {
        }

        private void testBtn_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = fileExecPath.Text.Trim(),
                    Arguments = fileExecArguments.Text,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "OmenFanPlus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void fileExecArguments_TextChanged(object sender, EventArgs e)
        {
        }
    }
}
