using MCDSaveEdit.Data;
using MCDSaveEdit.Services;
using System.Windows;
using System.Windows.Navigation;

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow : Window
    {
        private const string MCD_BUILDER_URL = "https://mcdbuilder.vercel.app";
        private const string DUNGEONS_HUB_URL = "https://dungeonss.vercel.app";
        private const string DUNGEONS2_HUB_URL = "https://dungeons2.vercel.app";
        private const string SOULS_GAME_URL = "https://soulss.vercel.app";

        public AboutWindow()
        {
            InitializeComponent();

            Title = R.ABOUT_WINDOW_TITLE;
            versionLabel.Content = R.formatVERSION(Config.instance.versionLabel(), Constants.CURRENT_VERSION.ToString());
        }

        private void mcdBuilderButton_Click(object sender, RoutedEventArgs e) => openAndClose("mcdBuilderButton_Click", MCD_BUILDER_URL);
        private void dungeonsHubButton_Click(object sender, RoutedEventArgs e) => openAndClose("dungeonsHubButton_Click", DUNGEONS_HUB_URL);
        private void dungeons2HubButton_Click(object sender, RoutedEventArgs e) => openAndClose("dungeons2HubButton_Click", DUNGEONS2_HUB_URL);
        private void soulsGameButton_Click(object sender, RoutedEventArgs e) => openAndClose("soulsGameButton_Click", SOULS_GAME_URL);

        private void openAndClose(string eventId, string url)
        {
            EventLogger.logEvent(eventId);
            LinkLauncher.open(url);
            this.Close();
        }

        /// <summary>
        /// Every Hyperlink in this window routes here. WPF raises RequestNavigate but does
        /// not act on it, so without this the links do nothing at all.
        /// </summary>
        private void hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            LinkLauncher.open(e.Uri?.AbsoluteUri);
            e.Handled = true;
        }
    }
}
