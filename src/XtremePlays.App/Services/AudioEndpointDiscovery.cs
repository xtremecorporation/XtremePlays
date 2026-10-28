using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace XtremePlays.App.Services;

public sealed record AudioEndpointInfo(string Id, string Name, string Direction, string State)
{
    public override string ToString() => $"{Direction}: {Name}";
}

public static class AudioEndpointDiscovery
{
    public static string? GetDefaultEndpointId(DataFlow direction, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(direction, role);
            return endpoint.ID;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    public static Task<IReadOnlyList<AudioEndpointInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AudioEndpointInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            var result = new List<AudioEndpointInfo>(endpoints.Count);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(new AudioEndpointInfo(endpoint.ID, endpoint.FriendlyName, endpoint.DataFlow.ToString(), endpoint.State.ToString()));
                }
            }

            return result.OrderBy(endpoint => endpoint.Direction).ThenBy(endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, cancellationToken);
}
