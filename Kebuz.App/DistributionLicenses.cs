using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Kebuz.Gui;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.App;

internal static class DistributionLicenses
{
    private static readonly string? Distribution = typeof(DistributionLicenses).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "ArchiveDistribution")?.Value;

    public static bool AcceptBeforeRunning()
    {
        if (Distribution is null)
        {
            return true;
        }

        var previousLanguage = Strings.Current;

        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "licenses");
            var sdk = File.ReadAllBytes(Path.Combine(folder, "Microsoft-Windows-SDK.rtf"));
            var runtime = Distribution == "portable"
                ? File.ReadAllBytes(Path.Combine(folder, "Microsoft-DotNET-Library.txt"))
                : null;
            if (sdk.Length == 0 || runtime is { Length: 0 })
            {
                throw new FormatException();
            }

            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? throw new UnauthorizedAccessException();
            using var terms = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            terms.AppendData(Encoding.UTF8.GetBytes(user));
            terms.AppendData(SHA256.HashData(sdk));
            if (runtime is not null)
            {
                terms.AppendData(SHA256.HashData(runtime));
            }

            var fingerprint = Convert.ToHexString(terms.GetHashAndReset());
            var acceptance = Path.Combine(AppPaths.Settings, $"license-accepted-{fingerprint}");
            if (File.Exists(acceptance))
            {
                return true;
            }

            Strings.Use(Strings.Resolve(Strings.System));
            if (!DistributionLicenseDialog.Show(sdk, runtime))
            {
                return false;
            }

            using var confirmation = File.Open(acceptance, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            Strings.Use(Strings.Resolve(Strings.System));
            MessageBox.Show(Strings.Get("License.Unavailable"), Strings.Get("License.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Strings.Use(previousLanguage);
        }
    }
}
