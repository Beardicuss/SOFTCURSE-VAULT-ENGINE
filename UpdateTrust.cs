namespace SoftcurseVaultCleaner
{
    /// <summary>
    /// Production update trust anchors. Public material is compiled into the
    /// signed application so downloaded metadata cannot replace it.
    /// </summary>
    internal static class UpdateTrust
    {
        public const string ManifestUrl =
            "https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE/releases/latest/download/update-envelope.json";

        // Provision real production public material before enabling updates.
        // The corresponding private keys must never enter this repository.
        public const string MetadataPublicKeySpkiBase64 = "";
        public const string InstallerSignerCertificateSha256 = "";

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(MetadataPublicKeySpkiBase64) &&
            InstallerSignerCertificateSha256.Length == 64;
    }
}
