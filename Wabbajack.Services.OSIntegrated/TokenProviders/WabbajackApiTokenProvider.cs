using System;
using System.Threading.Tasks;
using SharpCompress.Crypto;
using Wabbajack.DTOs.Logins;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Services.OSIntegrated.TokenProviders;

public class WabbajackApiTokenProvider : ITokenProvider<WabbajackApiState>
{
    private AbsolutePath MetricsPath => KnownFolders.WabbajackAppLocal.Combine("encrypted", "metrics-key");
    private AbsolutePath AuthorKeyPath => KnownFolders.WabbajackAppLocal.Combine("author-api-key.txt");

    public async ValueTask<WabbajackApiState?> Get()
    {
        if (!MetricsPath.FileExists())
            await CreateMetricsKey();

        string wjToken;
        try
        {
            wjToken = (await MetricsPath.FromEncryptedJsonFile<string>())!;
        }
        catch (CryptoException)
        {
            MetricsPath.Delete();
            await CreateMetricsKey();
            wjToken = (await MetricsPath.FromEncryptedJsonFile<string>())!;
        }

        return new WabbajackApiState
        {
            MetricsKey = wjToken,
            AuthorKey = AuthorKeyPath.FileExists() ? await AuthorKeyPath.ReadAllTextAsync() : null
        };
    }

    public ValueTask SetToken(WabbajackApiState val)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> TryDelete(WabbajackApiState val)
    {
        throw new NotImplementedException();
    }

    private async Task CreateMetricsKey()
    {
        var key = MakeRandomKey();
        await key.AsEncryptedJsonFile(MetricsPath);
    }

    public static string MakeRandomKey()
    {
        var random = new Random();
        var bytes = new byte[32];
        random.NextBytes(bytes);
        return bytes.ToHex();
    }
}