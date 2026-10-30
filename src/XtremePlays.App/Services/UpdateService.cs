using Velopack;
using Velopack.Sources;

namespace XtremePlays.App.Services;

public sealed class UpdateService
{
    private const string ReleasesRepository = "https://github.com/xtremecorporation/XtremePlays";
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private readonly UpdateManager _manager = new(new GithubSource(ReleasesRepository, accessToken: null, prerelease: false));

    public async Task<VelopackAsset?> CheckAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return null;
            }

            await _manager.DownloadUpdatesAsync(update);
            return update.TargetFullRelease;
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    public void ApplyUpdatesAndRestart(VelopackAsset update) => _manager.ApplyUpdatesAndRestart(update);
}
