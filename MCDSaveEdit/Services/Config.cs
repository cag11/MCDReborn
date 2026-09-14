using MCDSaveEdit.Data;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEdit.Services
{
    public class Config
    {
        //Update checks read the GitHub releases redirect - see downloadAsync() below.
        //This used to be a GameAnalyticsRemoteConfig reading STABLE_VER/BETA_VER
        //from GameAnalytics remote config, which busy-spun for 5s at every launch.
        public static Config instance = new Config();

        public bool isConfigsReady { get; protected set; }

        public bool showInventoryIndexOrEquipmentSlot {
            get {
                return Constants.IS_DEBUG;
            }
        }

        public bool showIDsInSelectionWindow {
            get {
                return Constants.IS_DEBUG;
            }
        }

        public string? stableReleaseVersionString { get; protected set; }
        public string? betaReleaseVersionString { get; protected set; }

        public virtual async Task downloadAsync()
        {
            //Using GitHub
            try
            {
                var request = WebRequest.Create(Constants.LATEST_RELEASE_GITHUB_URL);
                using var response = await request.GetResponseAsync();
                stableReleaseVersionString = response.ResponseUri.Segments.Last();
                betaReleaseVersionString = string.Empty;
                isConfigsReady = true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        public virtual string newVersionDownloadURL()
        {
            return Constants.LATEST_RELEASE_GITHUB_URL;
        }

        public string versionLabel()
        {
            if (!string.IsNullOrWhiteSpace(stableReleaseVersionString) && Constants.CURRENT_VERSION.Equals(new Version(stableReleaseVersionString!)))
            {
                return R.STABLE_VERSION_LABEL;
            }
            if (!string.IsNullOrWhiteSpace(betaReleaseVersionString) && Constants.CURRENT_VERSION.Equals(new Version(betaReleaseVersionString!)))
            {
                return R.BETA_VERSION_LABEL;
            }
            if(!string.IsNullOrWhiteSpace(betaReleaseVersionString) && Constants.CURRENT_VERSION > (new Version(betaReleaseVersionString!)))
            {
                return R.UNRELEASED_VERSION_LABEL;
            }
            //No release information at all - the update check has not run, failed, or
            //this fork has no published releases yet. Claiming "Old" would be a guess,
            //so report the build as unreleased instead.
            if (string.IsNullOrWhiteSpace(stableReleaseVersionString)
                && string.IsNullOrWhiteSpace(betaReleaseVersionString))
            {
                return R.UNRELEASED_VERSION_LABEL;
            }
            return R.OLD_VERSION_LABEL;
        }

        public bool isNewBetaVersionAvailable()
        {
            if (string.IsNullOrWhiteSpace(betaReleaseVersionString))
            {
                return false;
            }
            var betaVersion = new Version(betaReleaseVersionString!);
            return Constants.CURRENT_VERSION < betaVersion;
        }

        public bool isNewStableVersionAvailable()
        {
            if(string.IsNullOrWhiteSpace(stableReleaseVersionString))
            {
                return false;
            }
            var stableVersion = new Version(stableReleaseVersionString!);
            return Constants.CURRENT_VERSION < stableVersion;
        }

    }
}
