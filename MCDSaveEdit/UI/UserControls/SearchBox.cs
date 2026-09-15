using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// A text box for narrowing a long list, with the placeholder drawn behind it.
    ///
    /// WPF has no placeholder of its own, so it is a TextBlock under the box that hides as soon
    /// as anything is typed. Doing it here rather than at each call site is what keeps the three
    /// lists that use it looking and behaving the same.
    ///
    /// Escape clears rather than closing the window: a search that cannot be undone without
    /// starting over is worse than no search, and the window still closes on a second press
    /// because an empty box does not handle the key.
    /// </summary>
    public class SearchBox : Grid
    {
        private readonly TextBox _box = new TextBox();
        private readonly TextBlock _hint = new TextBlock();

        /// <summary>Raised whenever the term changes, so the list can be rebuilt.</summary>
        public event Action? changed;

        /// <summary>Raised on Down or Enter, for handing the keyboard to the list below.</summary>
        public event Action? advance;

        public string term => _box.Text?.Trim() ?? string.Empty;

        public SearchBox(string placeholder)
        {
            _box.Padding = new Thickness(6, 3, 6, 3);
            _box.FontSize = 12;
            _box.TextChanged += (s, e) => {
                _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                changed?.Invoke();
            };
            _box.PreviewKeyDown += onKeyDown;

            _hint.Text = placeholder;
            _hint.Margin = new Thickness(8, 0, 0, 0);
            _hint.FontSize = 12;
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.IsHitTestVisible = false;
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDisabled");

            Children.Add(_box);
            Children.Add(_hint);
        }

        private void onKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape when _box.Text.Length > 0:
                    _box.Clear();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Enter:
                    //Typing then pressing Down is how people expect to reach the first result.
                    advance?.Invoke();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Sets the term as if it had been typed. For capturing a filtered state.</summary>
        public void setTerm(string text) => _box.Text = text;

        public void focus()
        {
            //Focus has to wait for the window to be laid out, or it lands on nothing.
            Dispatcher.BeginInvoke(new Action(() => _box.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// Whether any of the given strings contains the term. An empty term matches everything,
        /// so a list is never emptied just by the box existing.
        ///
        /// Callers pass display names only. Matching internal ids as well reads as a bug from
        /// the outside: searching "wolf" would turn up Fox Armor, because its id is
        /// WolfArmor_Unique1.
        /// </summary>
        public bool matches(params string?[] fields)
        {
            var text = term;
            if (text.Length == 0) { return true; }

            foreach (var field in fields)
            {
                if (field != null && field.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
