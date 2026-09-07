using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Fixed lowercase storage identities; wire tokens never become path components.</summary>
    public static class LaunchStorageKeys
    {
        public static string Request(string requestId) => Hash(LaunchContractValues.Token(requestId, nameof(requestId)));
        public static string Observation(RuntimeObservationEnvelope observation) => Observation(observation.ProfileId,
            observation.ProfileRevision, observation.Producer, observation.PackageSetDigest);
        public static string Observation(string profileId, int profileRevision, PackageIdentity producer, string digest)
        {
            LaunchContractValues.Token(profileId, nameof(profileId));
            LaunchContractValues.Revision(profileRevision, nameof(profileRevision));
            if (producer == null) throw new ArgumentNullException(nameof(producer));
            LaunchContractValues.Digest(digest);
            // These validated ASCII tokens cannot contain JSON quotes, slashes, escapes or whitespace.
            return Hash("[\"" + profileId + "\"," + profileRevision.ToString(CultureInfo.InvariantCulture)
                + ",\"" + producer.Id + "\",\"" + producer.Version + "\",\"" + digest + "\"]");
        }
        private static string Hash(string value)
        {
            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(value));
            var result = new StringBuilder(64);
            foreach (var item in bytes) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }
}
