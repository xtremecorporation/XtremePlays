using Velopack;
using Velopack.Sources;

namespace XtremePlays.App.Services;

public sealed class UpdateService
{
    private const string ReleasesRepository = "https://github.com/xtremecorporation/XtremePlays";
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    public async Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleasesRepository, accessToken: null, prerelease: false));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return null;
            }

            await manager.DownloadUpdatesAsync(update);
            return update.TargetFullRelease.Version.ToString();
        }
        finally
        {
            UpdateLock.Release();
        }
    }
}
