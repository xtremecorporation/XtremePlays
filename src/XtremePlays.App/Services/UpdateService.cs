using Velopack;
using Velopack.Sources;

namespace XtremePlays.App.Services;

public sealed class UpdateService
{
    private const string ReleasesRepository = "https://github.com/xtremecorporation/XtremePlays";
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private readonly UpdateManager _manager = new(new GithubSource(ReleasesRepository, accessToken: null, prerelease: false));

    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    public async Task<VelopackAsset> DownloadUpdateAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            await _manager.DownloadUpdatesAsync(update, progress is null ? null : value => progress.Report(value), cancellationToken);
            return update.TargetFullRelease;
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    public void ApplyUpdatesAndRestart(VelopackAsset update) => _manager.ApplyUpdatesAndRestart(update);
}
