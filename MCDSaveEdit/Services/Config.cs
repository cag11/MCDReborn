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

        /// <summary>
        /// Where this build sits against what has been published: Stable, Beta, Unreleased, Old.
        ///
        /// The comparison is numeric throughout - System.Version, so 1.6.10.0 is correctly newer
        /// than 1.6.9.0, which a string comparison would get backwards.
        ///
        /// Unreleased means newer than everything published. That used to be tested against the
        /// beta string alone, and this fork publishes no betas, so the string was always empty
        /// and the test could never pass: a build ahead of the latest stable fell through to
        /// "Old" - which is how 1.6.3.0 came to describe itself as older than the 1.6.2.0 it
        /// replaces. Both channels are compared now, and either one being absent simply means
        /// there is nothing to be behind on that channel.
        /// </summary>
        public string versionLabel()
        {
            var stable = parseVersion(stableReleaseVersionString);
            var beta = parseVersion(betaReleaseVersionString);

            if (stable != null && Constants.CURRENT_VERSION.Equals(stable)) { return R.STABLE_VERSION_LABEL; }
            if (beta != null && Constants.CURRENT_VERSION.Equals(beta)) { return R.BETA_VERSION_LABEL; }

            //Ahead of every channel that reported a version. With neither reporting - the check
            //has not run, failed, or there are no releases yet - this is also the honest answer,
            //because calling a build "Old" with nothing to compare it to is a guess.
            var aheadOfStable = stable == null || Constants.CURRENT_VERSION > stable;
            var aheadOfBeta = beta == null || Constants.CURRENT_VERSION > beta;
            if (aheadOfStable && aheadOfBeta) { return R.UNRELEASED_VERSION_LABEL; }

            return R.OLD_VERSION_LABEL;
        }

        /// <summary>
        /// The version in a release tag, or null when there is not one.
        ///
        /// It comes off the end of a redirect URL, so it is whatever the tag happens to be
        /// named. Parsing it strictly would throw on the day someone tags "v1.7.0" instead of
        /// "1.7.0", and an About box is not worth crashing the app over.
        /// </summary>
        private static Version? parseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            return Version.TryParse(text!.TrimStart('v', 'V'), out var version) ? version : null;
        }

        public bool isNewBetaVersionAvailable()
        {
            var beta = parseVersion(betaReleaseVersionString);
            return beta != null && Constants.CURRENT_VERSION < beta;
        }

        public bool isNewStableVersionAvailable()
        {
            var stable = parseVersion(stableReleaseVersionString);
            return stable != null && Constants.CURRENT_VERSION < stable;
        }

    }
}
