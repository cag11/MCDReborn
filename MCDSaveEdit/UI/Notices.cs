using MCDSaveEdit.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Short messages in the corner of the main window, in place of a dialog that has to be
    /// clicked away.
    ///
    /// A dialog stops everything for something that needs no answer: "exported", "copied",
    /// "the game is running". Those come and go here on their own, and an error stays until it
    /// is closed, with a button to copy it. A QUESTION is still a dialog - that one needs the
    /// answer before anything can go on.
    ///
    /// Without a main window to show them in - a probe, the start before the window is up - they
    /// fall back to the dialog they replaced, so nothing is ever said to nobody.
    /// </summary>
    public static class Notices
    {
        public enum Kind { Done, Info, Warning, Error }

        private static NoticeHost? _host;

        /// <summary>Where notices appear. Set by the main window.</summary>
        internal static void showIn(NoticeHost host) => _host = host;

        public static void done(string text) => show(Kind.Done, text);

        /// <summary>A success about a file just written, with a button that shows it in Explorer.</summary>
        public static void doneWithFile(string text, string path)
            => show(Kind.Done, text, R.NOTICE_SHOW_FILE, () => reveal(path));

        public static void info(string text) => show(Kind.Info, text);

        public static void warn(string text) => show(Kind.Warning, text);

        /// <summary>Stays until closed: an error nobody saw is an error nobody can report.</summary>
        public static void error(string text) => show(Kind.Error, text);

        public static void show(Kind kind, string text, string? actionLabel = null, Action? action = null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => show(kind, text, actionLabel, action)));
                return;
            }

            if (_host == null || !_host.IsLoaded)
            {
                MessageBox.Show(text, kind == Kind.Error ? R.ERROR : R.APPLICATION_TITLE);
                return;
            }
            _host.add(kind, text, actionLabel, action);
        }

        private static void reveal(string path)
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception) { /* nothing worth saying: the notice already said where it went */ }
        }
    }

    /// <summary>
    /// The stack of notices, newest at the bottom. Built in code: each is a handful of elements
    /// coloured from the palette by reference, so a theme change repaints the ones showing.
    /// </summary>
    public sealed class NoticeHost : StackPanel
    {
        private const int MOST = 4;

        public NoticeHost()
        {
            VerticalAlignment = VerticalAlignment.Bottom;
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(0, 0, 18, 14);
            Width = 400;
        }

        internal void add(Notices.Kind kind, string text, string? actionLabel, Action? action)
        {
            //The same thing said again - the game-is-running notice, on every click - renews the
            //one showing rather than stacking copies of it.
            var same = Children.OfType<Notice>().FirstOrDefault(n => n.Kind == kind && n.Text == text && !n.Leaving);
            if (same != null) { same.renew(); return; }

            while (Children.OfType<Notice>().Count(n => !n.Leaving) >= MOST)
            {
                Children.OfType<Notice>().First(n => !n.Leaving).leave();
            }

            var notice = new Notice(kind, text, actionLabel, action, gone => Children.Remove(gone));
            Children.Add(notice);
        }
    }

    internal sealed class Notice : Grid
    {
        public Notices.Kind Kind { get; }
        public string Text { get; }
        public bool Leaving { get; private set; }

        private readonly DispatcherTimer? _timer;
        private readonly Action<Notice> _gone;

        public Notice(Notices.Kind kind, string text, string? actionLabel, Action? action, Action<Notice> gone)
        {
            Kind = kind;
            Text = text;
            _gone = gone;
            Margin = new Thickness(0, 8, 0, 0);

            var (glyph, colour) = kind switch
            {
                Notices.Kind.Done => ("", "Brush.Positive"),
                Notices.Kind.Warning => ("", "Brush.Warning"),
                Notices.Kind.Error => ("", "Brush.Danger"),
                _ => ("", "Brush.Select"),
            };

            //The shadow on a layer of its own, so the text above it is not blurred with it.
            var shadow = new Border { CornerRadius = new CornerRadius(8) };
            shadow.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
            var effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270 };
            //An effect is not an element and cannot follow a resource; it takes the palette's
            //values as they are now, which is all a notice that lives seconds needs.
            if (Application.Current?.TryFindResource("Color.Shadow") is Color shade) { effect.Color = shade; }
            if (Application.Current?.TryFindResource("Value.ShadowOpacity") is double strength) { effect.Opacity = strength; }
            shadow.Effect = effect;
            Children.Add(shadow);

            var card = new Border {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 8, 10),
            };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
            Children.Add(card);

            //The kind, as a bar down the left edge as well as an icon.
            var bar = new Border {
                Width = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(5, 10, 0, 10),
            };
            bar.SetResourceReference(Border.BackgroundProperty, colour);
            Children.Add(bar);

            var row = new DockPanel();
            card.Child = row;

            var icon = new TextBlock {
                Text = glyph, FontSize = 16, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, colour);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);

            var close = smallButton("", R.NOTICE_CLOSE, () => leave());
            close.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);

            var body = new StackPanel();
            var words = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 6, 0) };
            words.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            body.Children.Add(words);

            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (actionLabel != null && action != null)
            {
                actions.Children.Add(linkButton(actionLabel, () => { action(); leave(); }));
            }
            if (kind == Notices.Kind.Error)
            {
                actions.Children.Add(linkButton(R.NOTICE_COPY, () =>
                {
                    try { Clipboard.SetText(text); } catch (Exception) { }
                }));
            }
            if (actions.Children.Count > 0) { body.Children.Add(actions); }
            row.Children.Add(body);

            //Errors stay. The rest stay long enough to read: a few seconds and a little more
            //for every word, and not while the pointer is resting on them.
            if (kind != Notices.Kind.Error)
            {
                var seconds = Math.Min(14, 4 + text.Length / 25.0) + (kind == Notices.Kind.Warning ? 2 : 0);
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _timer.Tick += (_, _) => leave();
                _timer.Start();
                MouseEnter += (_, _) => _timer.Stop();
                MouseLeave += (_, _) => { if (!Leaving) { _timer.Start(); } };
            }

            //In: a short rise and fade, as Windows' own notifications do.
            Opacity = 0;
            var lift = new TranslateTransform(0, 12);
            RenderTransform = lift;
            Loaded += (_, _) =>
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
                lift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            };
        }

        public void renew()
        {
            if (_timer == null) { return; }
            _timer.Stop();
            _timer.Start();
        }

        public void leave()
        {
            if (Leaving) { return; }
            Leaving = true;
            _timer?.Stop();
            IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => _gone(this);
            BeginAnimation(OpacityProperty, fade);
        }

        private static Button smallButton(string glyph, string tip, Action act)
        {
            var button = new Button {
                Content = glyph, Width = 24, Height = 24, ToolTip = tip,
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, FontSize = 10,
            };
            button.SetResourceReference(Control.FontFamilyProperty, "Font.Icons");
            button.SetResourceReference(Control.ForegroundProperty, "Brush.TextMuted");
            button.Click += (_, _) => act();
            return button;
        }

        private static Button linkButton(string label, Action act)
        {
            var button = new Button {
                Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
            };
            button.Click += (_, _) => act();
            return button;
        }
    }
}
