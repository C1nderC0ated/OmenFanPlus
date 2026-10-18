using System;
using System.Drawing;
using Timer = System.Windows.Forms.Timer;

namespace OmenFanPlus.Forms
{
    /// <summary>The small on-screen notice for the Windows-key lock and touchpad toggle keys.</summary>
    public partial class FormNotify : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly Timer hideTimer = new() { Interval = 1000 };
        private readonly Timer fadeOutTimer = new() { Interval = 7 };

        public FormNotify()
        {
            InitializeComponent();

            hideTimer.Tick += (sender, e) =>
            {
                hideTimer.Stop();
                fadeOutTimer.Start();
            };

            fadeOutTimer.Tick += (sender, e) =>
            {
                Opacity -= 0.025;
                if (Opacity <= 0)
                {
                    fadeOutTimer.Stop();
                    Hide();
                }
            };
        }

        // The notice must never take the keyboard away from a game, or deactivate (and so close) the popup.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public delegate void NotifyDelegate(ButtonType button, bool isEnabled);
        public void Notify(ButtonType button, bool isEnabled)
        {
            if (button == ButtonType.Winkey)
            {
                if (isEnabled)
                    statusImage.Image = Resources.winkeyUnlocked;
                else
                    statusImage.Image = Resources.winkeyLocked;
            }
            if (button == ButtonType.Trackpad)
            {
                if (isEnabled)
                    statusImage.Image = Resources.trackpadUnlocked;
                else
                    statusImage.Image = Resources.trackpadLocked;
            }

            // Above the taskbar of the screen the mouse is on, whatever its scaling.
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            var margin = LogicalToDeviceUnits(12);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - margin);

            fadeOutTimer.Stop();
            Opacity = 1;
            Show();
            hideTimer.Stop();
            hideTimer.Start();
        }
    }

    public enum ButtonType
    {
        Winkey,
        Trackpad
    }
}
