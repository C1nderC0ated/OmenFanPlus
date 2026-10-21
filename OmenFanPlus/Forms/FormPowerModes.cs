using OmenFanPlus.Properties;
using OmenFanPlus.Services;

namespace OmenFanPlus.Forms
{
    /// <summary>One mode for the charger, one for the battery; the app switches when the power source changes.</summary>
    internal sealed class FormPowerModes : Form
    {
        private static readonly Color Background = Color.FromArgb(23, 23, 23);
        private static readonly Color TextColor = Color.FromArgb(230, 230, 230);
        private static readonly Color Muted = Color.FromArgb(160, 160, 160);
        private static readonly Color Field = Color.FromArgb(64, 64, 64);

        private readonly FanModeController _fanModes;
        private readonly CheckBox _enabled;
        private readonly ComboBox _charger;
        private readonly ComboBox _battery;

        /// <summary>True when OK was pressed and the modes are on, so the caller applies the one for the current source.</summary>
        public bool ApplyNow { get; private set; }

        public FormPowerModes(FanModeController fanModes)
        {
            _fanModes = fanModes;
            var calm = fanModes.Capabilities.QuietOrCoolMode;

            SuspendLayout();
            // Laid out in 96-dpi pixels; WinForms scales everything for the screen's scaling.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "OmenFanPlus - Charger and battery modes";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Background;
            ForeColor = TextColor;
            ClientSize = new Size(440, 286);
            Font = new Font("Segoe UI", 9F);

            _enabled = new CheckBox
            {
                Text = "Switch modes with the power source",
                Bounds = new Rectangle(16, 14, 408, 24),
                ForeColor = TextColor,
                Checked = Settings.Default.PowerModesEnabled,
            };
            Controls.Add(_enabled);

            _charger = AddRow(52, "On charger", calm, ModeChoices.Parse(Settings.Default.ChargerMode) ?? ModeChoice.Default);
            _battery = AddRow(86, "On battery", calm, ModeChoices.Parse(Settings.Default.BatteryMode) ?? ModeChoice.Comfort);

            Controls.Add(new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, 124, 408, 108),
                ForeColor = Muted,
                Text = "Applied at startup, after sleep and when you plug the charger in or out. Comfort, Default, " +
                       "Performance or Auto picked by hand (in the popup or with the OMEN key) is remembered for the " +
                       "power source you are on at the time; Max, Off and the custom curve last until the next plug " +
                       "or unplug. On most OMEN laptops the firmware refuses Performance on battery (then it isn't " +
                       "remembered); Auto never picks it there.",
            });

            var ok = MakeButton("OK", new Point(344, 242));
            ok.Click += (s, e) => Save();
            var cancel = MakeButton("Cancel", new Point(256, 242));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok;
            CancelButton = cancel;

            _enabled.CheckedChanged += (s, e) => _charger.Enabled = _battery.Enabled = _enabled.Checked;
            _charger.Enabled = _battery.Enabled = _enabled.Checked;
            ResumeLayout(false);
            PerformLayout();
        }

        private ComboBox AddRow(int top, string caption, Hp.Omen.OmenCommonLib.PowerControl.Enum.PerformanceMode calm, ModeChoice selected)
        {
            Controls.Add(new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, top + 3, 120, 22),
                ForeColor = TextColor,
                Text = caption,
            });
            var combo = new ComboBox
            {
                Bounds = new Rectangle(140, top, 180, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Field,
                ForeColor = TextColor,
            };
            foreach (var choice in ModeChoices.All)
                combo.Items.Add(new Item(choice, ModeChoices.DisplayName(choice, calm)));
            combo.SelectedIndex = Array.IndexOf(ModeChoices.All, selected);
            Controls.Add(combo);
            return combo;
        }

        private Button MakeButton(string text, Point location)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = TextColor,
                BackColor = Field,
                FlatAppearance = { BorderSize = 0 },
            };
            Controls.Add(button);
            return button;
        }

        private void Save()
        {
            Settings.Default.PowerModesEnabled = _enabled.Checked;
            Settings.Default.ChargerMode = ModeChoices.Key(((Item)_charger.SelectedItem!).Choice);
            Settings.Default.BatteryMode = ModeChoices.Key(((Item)_battery.SelectedItem!).Choice);
            FanModeController.TrySave();
            ApplyNow = _enabled.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed record Item(ModeChoice Choice, string Name)
        {
            public override string ToString() => Name;
        }
    }
}
