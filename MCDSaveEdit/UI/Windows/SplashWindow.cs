using MCDSaveEdit.Data;
using MCDSaveEdit.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Startup window. <see cref="textbox"/> is still the sink ControlWriter writes the whole
    /// console log into - that plumbing is unchanged - but it is hidden, and only the most
    /// recent line is surfaced as a status caption.
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow() : base()
        {
            InitializeComponent();

            titleLabel.Text = R.APPLICATION_TITLE;
            versionLabel.Text = $"Version {Constants.CURRENT_VERSION}";

            //Mirror the log's last meaningful line into the caption.
            textbox.TextChanged += textbox_TextChanged;
        }

        private void textbox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var latest = lastMeaningfulLine(textbox.Text);
            if (latest != null) { statusLabel.Text = latest; }
        }

        /// <summary>
        /// The last non-blank line. Log lines arrive with timestamps and level prefixes from
        /// other writers, so anything that is only punctuation is skipped rather than shown.
        /// </summary>
        private static string? lastMeaningfulLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }

            var lines = text!.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) { continue; }
                if (!line.Any(char.IsLetterOrDigit)) { continue; }
                return line;
            }
            return null;
        }
    }
}
