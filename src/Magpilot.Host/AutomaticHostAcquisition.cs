using Magpilot.Shared.Models;

namespace Magpilot.Host;

internal static class AutomaticHostAcquisition
{
    public static async Task<SessionStateInfo> AcquireThenNotifyAsync(
        Func<Task<SessionStateInfo>> acquire,
        Func<Task> notify,
        Action<Exception> reportNotificationFailure)
    {
        // A guessed session may belong to a different live host whose
        // Copilot process left no lock file. Only the lease is authoritative.
        var state = await acquire();
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            reportNotificationFailure(ex);
        }
        return state;
    }
}
