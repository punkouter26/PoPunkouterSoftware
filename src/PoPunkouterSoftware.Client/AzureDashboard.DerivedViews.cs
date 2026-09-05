using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

// Partial class — the one derived view that is NOT pure, because it reads the page's own
// snooze set. Everything else that used to live here (the safe-to-remove analysis, the
// per-app rollup, the priority queue, the resource explorer rows and the inference helpers)
// moved to DashboardDerivations, a public static class, so it can be unit-tested; see the
// note at the top of that file.
public partial class AzureDashboard
{
    // Filtered against _snoozedKeys here rather than at the safeToRemove/BuildSafeToRemove
    // source: safeToRemove also feeds BuildPriorityQueue (as "SafeToRemove"-sourced items,
    // keyed "{Source}|{Item}") and BuildResourceExplorerItems' "Waste" risk flag — neither of
    // which should be affected by a snooze scoped to this list's own dedup key
    // ({Type}|{ResourceGroup}|{Name}).
    private List<SafeToRemoveItem> CleanupCandidates =>
        safeToRemove
            .Where(i => !_snoozedKeys.Contains($"{i.Type}|{i.ResourceGroup}|{i.Name}"))
            .OrderBy(i => SeverityLevel.Rank(i.Confidence))
            .ThenBy(i => i.Type)
            .ThenBy(i => i.Name)
            .ToList();
}
