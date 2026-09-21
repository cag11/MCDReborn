using System.Windows;
#nullable enable

namespace MCDSaveEdit.UI.Windows
{
    /// <summary>
    /// Asks for one line of text.
    ///
    /// WPF has no input box, so the choice is to grow one or to reach for
    /// Microsoft.VisualBasic.Interaction.InputBox - which drags a whole assembly in for a
    /// textbox and looks like a different application when it opens. This is the smaller cost,
    /// and it is here rather than inside the tab that needed it first because the next prompt
    /// should not have to repeat it.
    /// </summary>
    public partial class AskWindow : Window
    {
        public AskWindow(string title, string question, string? already = null,
            string? hint = null)
        {
            InitializeComponent();

            Title = title;
            whatLabel.Text = question;
            answerBox.Text = already ?? string.Empty;

            hintLabel.Text = hint ?? string.Empty;
            hintLabel.Visibility = string.IsNullOrEmpty(hint)
                ? Visibility.Collapsed
                : Visibility.Visible;

            //Selected, not just filled in. Somebody renaming a thing usually wants a different
            //name rather than an edited one, and a preselected value means they can simply type.
            Loaded += (_, _) =>
            {
                answerBox.Focus();
                answerBox.SelectAll();
            };
        }

        /// <summary>What they typed, trimmed. Only meaningful when ShowDialog returned true.</summary>
        public string Answer => answerBox.Text.Trim();

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            //Nothing typed is a cancel rather than an error. An empty name is never what somebody
            //meant, and refusing it with a message box for a one-field dialog is a lot of
            //ceremony to say "you left it blank".
            if (Answer.Length == 0) { return; }

            DialogResult = true;
        }
    }
}
