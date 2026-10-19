namespace OmenFanPlus.Services
{
    internal enum UpdateComponent
    {
        DotNetRuntime,
        PawnIo,
    }

    /// <summary>
    /// What is fixed about each dependency: who signs its installer, how it is run silently, and the package names
    /// for the manual instructions. Nothing of this is ever taken from data that arrived over the network.
    /// </summary>
    internal sealed record UpdateCatalogEntry(
        string DisplayName,
        string HashAlgorithm,
        string InstallerArguments,
        string SignerOrganization,
        string ChocoPackage,
        string WingetId);

    internal static class UpdateCatalog
    {
        public static UpdateCatalogEntry Describe(UpdateComponent component) => component switch
        {
            UpdateComponent.DotNetRuntime => new UpdateCatalogEntry(".NET Desktop Runtime", "SHA512",
                "/install /quiet /norestart", "Microsoft Corporation",
                "dotnet-10.0-desktopruntime", "Microsoft.DotNet.DesktopRuntime.10"),
            UpdateComponent.PawnIo => new UpdateCatalogEntry("PawnIO", "SHA256",
                "-install -silent", "namazso",
                "pawnio", "namazso.PawnIO"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
        };
    }

    /// <summary>
    /// A newer release of something the app depends on, with what is needed to install it safely: where the
    /// publisher serves the installer and the hash the publisher lists for it (always required; a release whose
    /// hash can't be read is reported but not installed).
    /// </summary>
    internal sealed record AvailableUpdate(UpdateComponent Component, Version Version, Uri DownloadUrl, string ExpectedHash)
    {
        public UpdateCatalogEntry Info => UpdateCatalog.Describe(Component);
        public string DisplayName => Info.DisplayName;
        public string HashAlgorithm => Info.HashAlgorithm;
        public string SignerOrganization => Info.SignerOrganization;

        public static AvailableUpdate DotNetRuntime(Version version, Uri url, string sha512) =>
            new(UpdateComponent.DotNetRuntime, version, url, sha512);

        public static AvailableUpdate PawnIo(Version version, Uri url, string sha256) =>
            new(UpdateComponent.PawnIo, version, url, sha256);

        /// <summary>The file name the installer is saved under: the publisher's, if it is a plain .exe name.</summary>
        public string FileName
        {
            get
            {
                var name = Path.GetFileName(DownloadUrl.LocalPath);
                var plain = name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
                return plain ? name : $"{Component}-{Version}.exe";
            }
        }
    }
}
