using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace OmenFanPlus.Services
{
    internal readonly record struct SignatureCheck(bool Valid, string Detail, string? Subject);

    /// <summary>
    /// Checks a downloaded installer before anything runs it: its hash against what the publisher's release
    /// metadata lists, and its Authenticode signature (Windows must find the signature valid and trusted, and the
    /// signing certificate must belong to the expected organization).
    /// </summary>
    internal static class InstallerVerifier
    {
        public static async Task<string> ComputeHashAsync(Stream stream, string algorithm, CancellationToken ct)
        {
            var hash = algorithm.ToUpperInvariant() switch
            {
                "SHA512" => await SHA512.HashDataAsync(stream, ct),
                "SHA256" => await SHA256.HashDataAsync(stream, ct),
                _ => throw new NotSupportedException($"Unknown hash algorithm {algorithm}"),
            };
            return Convert.ToHexString(hash);
        }

        public static bool HashMatches(string? expected, string actual) =>
            !string.IsNullOrWhiteSpace(expected) &&
            string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);

        /// <summary>"sha256:0123..." as GitHub's API gives it. Null when the text isn't of that form.</summary>
        public static (string Algorithm, string Hex)? ParseDigest(string? digest)
        {
            if (string.IsNullOrWhiteSpace(digest))
                return null;
            var colon = digest.IndexOf(':');
            if (colon <= 0 || colon == digest.Length - 1)
                return null;

            var algorithm = digest[..colon].Trim().ToUpperInvariant().Replace("-", "");
            var hex = digest[(colon + 1)..].Trim();
            var expectedLength = algorithm switch { "SHA256" => 64, "SHA512" => 128, _ => -1 };
            if (expectedLength < 0 || hex.Length != expectedLength || !hex.All(Uri.IsHexDigit))
                return null;
            return (algorithm, hex);
        }

        /// <summary>
        /// Windows' own Authenticode check (chain to a trusted root, file digest intact), then the signer's
        /// organization. Revocation isn't looked up: the hash check against the publisher's metadata covers the
        /// file's identity, and an offline revocation lookup would only fail good files.
        /// </summary>
        public static SignatureCheck VerifySignature(string path, string expectedOrganization)
        {
            var status = WinVerifyTrustFile(path);
            if (status != 0)
                return new SignatureCheck(false, $"Windows doesn't trust the signature (0x{status:X8})", null);

            X509Certificate2? signer;
            try
            {
                signer = ReadSignerCertificate(path);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or BadImageFormatException)
            {
                return new SignatureCheck(false, $"the signer certificate can't be read ({ex.Message})", null);
            }

            using (signer)
            {
                if (signer == null)
                    return new SignatureCheck(false, "the file has no embedded signature", null);

                var organization = OrganizationOf(signer.SubjectName);
                if (!string.Equals(organization, expectedOrganization, StringComparison.Ordinal))
                    return new SignatureCheck(false, $"signed by \"{signer.Subject}\", not by {expectedOrganization}", signer.Subject);

                return new SignatureCheck(true, $"signed by {organization}", signer.Subject);
            }
        }

        /// <summary>The O= part of a distinguished name, or null when it has none.</summary>
        internal static string? OrganizationOf(X500DistinguishedName name)
        {
            foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            {
                if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10")
                    return rdn.GetSingleElementValue();
            }
            return null;
        }

        /// <summary>
        /// The certificate of the (first) Authenticode signer, read from the PE file's certificate table. Null for
        /// a file without an embedded signature.
        /// </summary>
        internal static X509Certificate2? ReadSignerCertificate(string path)
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(file);
            var directory = pe.PEHeaders.PEHeader?.CertificateTableDirectory;
            // The certificate table's "RVA" is a file offset. Each entry: dwLength, wRevision, wCertificateType, data.
            if (directory is not { Size: >= 8 } table || table.RelativeVirtualAddress <= 0 ||
                table.RelativeVirtualAddress + (long)table.Size > file.Length)
                return null;

            var bytes = new byte[table.Size];
            file.Position = table.RelativeVirtualAddress;
            file.ReadExactly(bytes);

            var length = BitConverter.ToInt32(bytes, 0);
            var type = BitConverter.ToUInt16(bytes, 6);
            const ushort WinCertTypePkcsSignedData = 2;
            if (type != WinCertTypePkcsSignedData || length < 8 || length > bytes.Length)
                return null;

            var cms = new SignedCms();
            cms.Decode(bytes.AsSpan(8, length - 8).ToArray());
            return cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
        }

        // ---- WinVerifyTrust -------------------------------------------------------------------------------

        private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdRevocationCheckNone = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WintrustData data);

        /// <summary>0 when Windows finds a valid, trusted Authenticode signature on the file; else the HRESULT.</summary>
        internal static int WinVerifyTrustFile(string path)
        {
            var fileInfo = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = Path.GetFullPath(path),
            };
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPtr,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = WtdRevocationCheckNone,
                };
                var action = WintrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                // Releases the state the verify call allocated.
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<WintrustFileInfo>(fileInfoPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }
    }
}
