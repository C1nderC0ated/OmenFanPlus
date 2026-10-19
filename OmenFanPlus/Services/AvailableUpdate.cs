namespace OmenFanPlus.Services
{
    internal enum UpdateComponent
    {
        DotNetRuntime,
        PawnIo,
        /// <summary>OmenFanPlus itself, from its GitHub releases.</summary>
        App,
    }

    /// <summary>
    /// What is fixed about each thing the app can update: who signs its installer (null for the app's own zip,
    /// which carries no signature), how it is run silently, and the package names for the manual instructions.
    /// Nothing of this is ever taken from data that arrived over the network.
    /// </summary>
    internal sealed record UpdateCatalogEntry(
        string DisplayName,
        string HashAlgorithm,
        string InstallerArguments,
        string? SignerOrganization,
        string ChocoPackage,
        string WingetId);

    internal static class UpdateCatalog
    {
        /// <summary>The GitHub repository whose releases are OmenFanPlus's own updates.</summary>
        public const string AppRepository = "C1nderC0ated/OmenFanPlus";

        public static UpdateCatalogEntry Describe(UpdateComponent component) => component switch
        {
            UpdateComponent.DotNetRuntime => new UpdateCatalogEntry(".NET Desktop Runtime", "SHA512",
                "/install /quiet /norestart", "Microsoft Corporation",
                "dotnet-10.0-desktopruntime", "Microsoft.DotNet.DesktopRuntime.10"),
            UpdateComponent.PawnIo => new UpdateCatalogEntry("PawnIO", "SHA256",
                "-install -silent", "namazso",
                "pawnio", "namazso.PawnIO"),
            UpdateComponent.App => new UpdateCatalogEntry("OmenFanPlus", "SHA256", "", null, "", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
        };
    }

    /// <summary>
    /// A newer release of something the app can update, with what is needed to install it safely: where the
    /// publisher serves the file and the hash the publisher lists for it (always required; a release whose hash
    /// can't be read is reported but not installed).
    /// </summary>
    internal sealed record AvailableUpdate(UpdateComponent Component, Version Version, Uri DownloadUrl, string ExpectedHash)
    {
        public UpdateCatalogEntry Info => UpdateCatalog.Describe(Component);
        public string DisplayName => Info.DisplayName;
        public string HashAlgorithm => Info.HashAlgorithm;
        public string? SignerOrganization => Info.SignerOrganization;

        public static AvailableUpdate DotNetRuntime(Version version, Uri url, string sha512) =>
            new(UpdateComponent.DotNetRuntime, version, url, sha512);

        public static AvailableUpdate PawnIo(Version version, Uri url, string sha256) =>
            new(UpdateComponent.PawnIo, version, url, sha256);

        public static AvailableUpdate App(Version version, Uri zipUrl, string sha256) =>
            new(UpdateComponent.App, version, zipUrl, sha256);

        /// <summary>The file name the download is saved under: the publisher's, if it is a plain .exe/.zip name.</summary>
        public string FileName
        {
            get
            {
                var name = Path.GetFileName(DownloadUrl.LocalPath);
                var extension = Component == UpdateComponent.App ? ".zip" : ".exe";
                var plain = name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
                            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
                return plain ? name : $"{Component}-{Version}{extension}";
            }
        }
    }
}
