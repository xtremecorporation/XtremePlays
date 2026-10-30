using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XtremePlays.App.Services;

public sealed class AdBlockService : IDisposable
{
    private const long MaximumListSize = 8 * 1024 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly (string FileName, string Url)[] FilterLists =
    [
        ("easylist.txt", "https://easylist.to/easylist/easylist.txt"),
        ("easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt")
    ];

    private readonly HttpClient _httpClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly SemaphoreSlim _enableLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _engineLock = new();
    private NativeAdBlockEngine? _engine;
    private bool _disposed;
    private long _generation;

    public bool IsEnabled
    {
        get { lock (_engineLock) return _engine is not null; }
    }

    public async Task<int> EnableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linkedCancellation.Token;
        await _enableLock.WaitAsync(cancellationToken);
        try
        {
            if (IsEnabled)
            {
                return await CountRulesAsync(cancellationToken);
            }

            var cachedLists = await ReadCachedListsAsync(cancellationToken);
            var hasAllLists = cachedLists.Count == FilterLists.Length;
            var cacheIsFresh = hasAllLists && FilterLists.All(list => IsFresh(CachePath(list.FileName)));
            if (cachedLists.Count == 0)
            {
                cachedLists = await DownloadListsAsync(cancellationToken);
                await SaveListsAsync(cachedLists, cancellationToken);
            }

            if (cachedLists.Count == 0)
            {
                throw new IOException("No usable ad filter lists are available. Connect to the internet and try again.");
            }

            var replacement = await Task.Run(() => NativeAdBlockEngine.Create(string.Join("\n", cachedLists.Values)), cancellationToken);
            if (_disposed)
            {
                replacement.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }

            NativeAdBlockEngine? oldEngine;
            long generation;
            lock (_engineLock)
            {
                oldEngine = _engine;
                _engine = replacement;
                generation = ++_generation;
            }
            oldEngine?.Dispose();

            if (!cacheIsFresh)
            {
                _ = RefreshListsInBackgroundAsync(generation, _lifetime.Token);
            }

            return cachedLists.Values.Sum(CountRules);
        }
        finally
        {
            _enableLock.Release();
        }
    }

    public bool ShouldBlock(string url, string sourceUrl, string requestType, string method)
    {
        lock (_engineLock)
        {
            return _engine?.ShouldBlock(url, sourceUrl, requestType, method) == true;
        }
    }

    public void Disable()
    {
        NativeAdBlockEngine? engine;
        lock (_engineLock)
        {
            ++_generation;
            engine = _engine;
            _engine = null;
        }
        engine?.Dispose();
    }

    private async Task RefreshListsInBackgroundAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            var freshLists = await DownloadListsAsync(cancellationToken);
            if (freshLists.Count != FilterLists.Length) return;

            var replacement = await Task.Run(() => NativeAdBlockEngine.Create(string.Join("\n", freshLists.Values)), cancellationToken);
            try
            {
                await SaveListsAsync(freshLists, cancellationToken);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }
            NativeAdBlockEngine? oldEngine = null;
            lock (_engineLock)
            {
                if (_engine is null || _generation != generation)
                {
                    replacement.Dispose();
                    return;
                }

                oldEngine = _engine;
                _engine = replacement;
                ++_generation;
            }

            oldEngine?.Dispose();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException or DllNotFoundException or BadImageFormatException or ObjectDisposedException)
        {
            // Keep using the last valid local rules when an online refresh is unavailable.
        }
    }

    private async Task<Dictionary<string, string>> ReadCachedListsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in FilterLists)
        {
            var path = CachePath(list.FileName);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > MaximumListSize) continue;
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                if (IsUsableList(text)) result[list.FileName] = text;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // A damaged or inaccessible cache entry is replaced by a fresh download below.
            }
        }
        return result;
    }

    private async Task<Dictionary<string, string>> DownloadListsAsync(CancellationToken cancellationToken)
    {
        var downloads = await Task.WhenAll(FilterLists.Select(async list =>
        {
            try
            {
                return (list.FileName, Text: await DownloadListAsync(list.Url, cancellationToken));
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or DecoderFallbackException)
            {
                return (list.FileName, Text: (string?)null);
            }
        }));

        return downloads.Where(item => item.Text is not null)
            .ToDictionary(item => item.FileName, item => item.Text!, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> DownloadListAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumListSize)
        {
            throw new IOException("The filter list exceeded the allowed download size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumListSize) throw new IOException("The filter list exceeded the allowed download size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var text = new UTF8Encoding(false, true).GetString(output.ToArray());
        if (!IsUsableList(text)) throw new IOException("The filter list response did not contain valid filter rules.");
        return text;
    }

    private static bool IsUsableList(string text) =>
        text.Length >= 1000 &&
        (text.StartsWith("[Adblock Plus 2.0]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("! Title:", StringComparison.OrdinalIgnoreCase)) &&
        !text.Contains("<html", StringComparison.OrdinalIgnoreCase) &&
        text.Count(character => character == '\n') >= 100;

    private static int CountRules(string text) => text.Split('\n')
        .Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('!') && !line.TrimStart().StartsWith('['));

    private static bool IsFresh(string path) => File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RefreshInterval;

    private static string CachePath(string fileName) => Path.Combine(AppPaths.DataDirectory, "AdBlockLists", fileName);

    private static async Task SaveListsAsync(Dictionary<string, string> lists, CancellationToken cancellationToken)
    {
        foreach (var list in lists)
        {
            var path = CachePath(list.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, list.Value, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
    }

    private async Task<int> CountRulesAsync(CancellationToken cancellationToken)
    {
        var lists = await ReadCachedListsAsync(cancellationToken);
        return lists.Values.Sum(CountRules);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _httpClient.Dispose();
        Disable();
        _lifetime.Dispose();
    }

    private sealed class NativeAdBlockEngine : IDisposable
    {
        private readonly NativeEngineHandle _handle;

        private NativeAdBlockEngine(NativeEngineHandle handle) => _handle = handle;

        public static NativeAdBlockEngine Create(string filters)
        {
            var pointer = NativeMethods.CreateEngine(filters);
            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("The Brave ad-block filter engine could not load the filter lists.");
            }
            return new NativeAdBlockEngine(new NativeEngineHandle(pointer));
        }

        public bool ShouldBlock(string url, string sourceUrl, string requestType, string method) =>
            NativeMethods.ShouldBlock(_handle, url, sourceUrl, requestType, method) != 0;

        public void Dispose() => _handle.Dispose();
    }

    private sealed class NativeEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeEngineHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            NativeMethods.DestroyEngine(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        [DllImport("xtreme_adblock_bridge", EntryPoint = "xtreme_adblock_engine_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr CreateEngine([MarshalAs(UnmanagedType.LPUTF8Str)] string filters);

        [DllImport("xtreme_adblock_bridge", EntryPoint = "xtreme_adblock_engine_should_block", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ShouldBlock(NativeEngineHandle engine,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceUrl,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestType,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string method);

        [DllImport("xtreme_adblock_bridge", EntryPoint = "xtreme_adblock_engine_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void DestroyEngine(IntPtr engine);
    }
}
