using System.Collections.Immutable;
using MediaDashboard.Core;
using MediaDashboard.Platform.Interop;
using static MediaDashboard.Platform.Interop.DisplayConfigNative;

namespace MediaDashboard.Platform;

/// <summary>Connected Displays plus the Layout Windows is currently showing.</summary>
public sealed record DisplaySnapshot(ImmutableArray<DisplayInfo> Connected, DisplayLayout Layout);

/// <summary>Opaque copy of the active display configuration (raw CCD paths + modes), for reverting.</summary>
public sealed class SavedDisplayConfig
{
    internal SavedDisplayConfig(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        Paths = paths;
        Modes = modes;
    }

    internal DISPLAYCONFIG_PATH_INFO[] Paths { get; }
    internal DISPLAYCONFIG_MODE_INFO[] Modes { get; }
}

public sealed class DisplayConfigException(string message, int win32Error) : Exception(message)
{
    /// <summary>Win32 error code returned by the CCD API, or 0 when the requested Layout itself is not realisable.</summary>
    public int Win32Error { get; } = win32Error;
}

/// <summary>Reads and changes the Windows display configuration through the CCD API.</summary>
public sealed class DisplayConfigService
{
    private const uint ApplyFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE;
    private static readonly PixelSize FallbackSize = new(1920, 1080);

    public DisplaySnapshot Read()
    {
        var all = Query(QDC_ALL_PATHS);
        var adapterKeys = new Dictionary<LUID, string>();
        var connected = new Dictionary<DisplayId, DisplayInfo>();

        foreach (var path in all.Paths)
        {
            var target = path.targetInfo;
            if (target.targetAvailable == 0) continue;
            var name = GetTargetName(target.adapterId, target.id);
            if (name.DevicePath.Length == 0) continue;
            var id = new DisplayId(name.DevicePath);
            if (connected.ContainsKey(id)) continue;

            if (!adapterKeys.TryGetValue(target.adapterId, out var adapterKey))
                adapterKeys[target.adapterId] = adapterKey = GetAdapterName(target.adapterId);

            var size = GetPreferredSize(target.adapterId, target.id) ?? CurrentSourceSize(all, path) ?? FallbackSize;
            var model = name.FriendlyName.Length > 0 ? name.FriendlyName : "Display";
            connected[id] = new DisplayInfo(id, model, adapterKey, size);
        }

        var infos = connected.Values.OrderBy(d => d.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        return new DisplaySnapshot(infos, ReadLayout(Query(QDC_ONLY_ACTIVE_PATHS)));
    }

    public SavedDisplayConfig Capture()
    {
        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        return new SavedDisplayConfig(active.Paths, active.Modes);
    }

    public void Restore(SavedDisplayConfig saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Set(saved.Paths, saved.Modes, "Restoring the display configuration");
    }

    /// <summary>
    /// Makes Windows show <paramref name="desired"/>: exactly its items enabled, each Mirror Group sharing one source,
    /// positioned as given. Displays newly enabled get the mode Windows picks; positions are then re-flowed with the
    /// actual sizes via <see cref="LayoutEngine.Relayout"/>.
    /// </summary>
    public void Apply(DisplayLayout desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Items.IsEmpty) throw new DisplayConfigException("The Layout has no enabled Displays.", 0);
        desired = WithPrimaryAtOrigin(desired);

        var all = Query(QDC_ALL_PATHS);
        var targets = MapTargets(all.Paths);
        var plan = PlanSources(desired, all.Paths, targets);

        var paths = new List<DISPLAYCONFIG_PATH_INFO>();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();
        bool windowsPicksModes = false;

        // Primary first: Windows treats the path order as priority when it has to drop something.
        foreach (var (item, source) in plan.OrderByDescending(p => p.Item.IsPrimary))
        {
            var itemPaths = item.Members.Select(m => all.Paths[source.PathTo[m]]).ToArray();
            // Reuse modes only when this exact source already drives every member; otherwise let Windows choose.
            bool reuse = itemPaths.All(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0
                                            && p.sourceInfo.modeInfoIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                                            && p.targetInfo.modeInfoIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
            uint sourceModeIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            if (reuse)
            {
                var sourceMode = all.Modes[itemPaths[0].sourceInfo.modeInfoIdx];
                sourceMode.sourceMode.position = new POINTL { x = item.Bounds.X, y = item.Bounds.Y };
                sourceModeIdx = (uint)modes.Count;
                modes.Add(sourceMode);
            }
            else
            {
                windowsPicksModes = true;
            }

            foreach (var original in itemPaths)
            {
                var path = original;
                path.flags = DISPLAYCONFIG_PATH_ACTIVE;
                path.sourceInfo.modeInfoIdx = sourceModeIdx;
                if (reuse)
                {
                    path.targetInfo.modeInfoIdx = (uint)modes.Count;
                    modes.Add(all.Modes[original.targetInfo.modeInfoIdx]);
                }
                else
                {
                    path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                }
                paths.Add(path);
            }
        }

        Set([.. paths], [.. modes], "Applying the Layout");

        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        var sources = LocateActiveSources(desired, active);
        bool sizesDiffer = desired.Items.Zip(sources).Any(pair =>
        {
            var mode = active.Modes[pair.Second].sourceMode;
            return mode.width != pair.First.Bounds.Width || mode.height != pair.First.Bounds.Height;
        });
        if (!windowsPicksModes && !sizesDiffer) return;

        var actual = new DisplayLayout(desired.Items.Zip(sources, (item, modeIdx) =>
        {
            var mode = active.Modes[modeIdx].sourceMode;
            return item with { Bounds = item.Bounds with { Width = (int)mode.width, Height = (int)mode.height } };
        }).ToImmutableArray());
        var flowed = LayoutEngine.Relayout(actual);

        bool moved = false;
        for (int i = 0; i < flowed.Items.Length; i++)
        {
            ref var mode = ref active.Modes[sources[i]].sourceMode;
            var bounds = flowed.Items[i].Bounds;
            if (mode.position.x == bounds.X && mode.position.y == bounds.Y) continue;
            mode.position = new POINTL { x = bounds.X, y = bounds.Y };
            moved = true;
        }
        if (moved) Set(active.Paths, active.Modes, "Positioning the Displays");
    }

    private static DisplayLayout WithPrimaryAtOrigin(DisplayLayout layout)
    {
        var primary = layout.Primary ?? layout.Items[0];
        int dx = -primary.Bounds.X, dy = -primary.Bounds.Y;
        return new DisplayLayout(layout.Items.Select(i => i with
        {
            Bounds = i.Bounds.Offset(dx, dy),
            IsPrimary = ReferenceEquals(i, primary),
        }).ToImmutableArray());
    }

    private sealed record SourcePlan(LUID Adapter, uint SourceId, Dictionary<DisplayId, int> PathTo);

    /// <summary>Picks one CCD source per Layout item; every member gets a path from that source.</summary>
    private static List<(LayoutItem Item, SourcePlan Source)> PlanSources(
        DisplayLayout desired, DISPLAYCONFIG_PATH_INFO[] all, Dictionary<DisplayId, (LUID Adapter, uint TargetId)> targets)
    {
        var seen = new HashSet<DisplayId>();
        foreach (var member in desired.Items.SelectMany(i => i.Members))
        {
            if (!targets.ContainsKey(member))
                throw new DisplayConfigException($"Display '{member}' is not connected.", 0);
            if (!seen.Add(member))
                throw new DisplayConfigException($"Display '{member}' appears in more than one Layout item.", 0);
        }

        // (adapter, source, target) -> path index; the first occurrence wins, so active paths are preferred.
        var pathIndex = new Dictionary<(LUID, uint, uint), int>();
        for (int i = 0; i < all.Length; i++)
        {
            var p = all[i];
            if (p.targetInfo.targetAvailable == 0 || !p.sourceInfo.adapterId.Equals(p.targetInfo.adapterId)) continue;
            pathIndex.TryAdd((p.sourceInfo.adapterId, p.sourceInfo.id, p.targetInfo.id), i);
        }

        var adapters = new Dictionary<LayoutItem, LUID>();
        foreach (var item in desired.Items)
        {
            if (item.Members.IsEmpty) throw new DisplayConfigException("A Layout item has no Displays.", 0);
            var adapter = targets[item.Members[0]].Adapter;
            if (item.Members.Any(m => !targets[m].Adapter.Equals(adapter)))
                throw new DisplayConfigException(
                    $"Mirror Group {string.Join(" + ", item.Members)} spans graphics adapters; Windows can only mirror Displays on one adapter.", 0);
            adapters[item] = adapter;
        }

        Dictionary<DisplayId, int>? PathsFrom(LayoutItem item, uint sourceId)
        {
            var result = new Dictionary<DisplayId, int>();
            foreach (var m in item.Members)
            {
                var (adapter, targetId) = targets[m];
                if (!pathIndex.TryGetValue((adapter, sourceId, targetId), out int idx)) return null;
                result[m] = idx;
            }
            return result;
        }

        var assigned = new Dictionary<LayoutItem, SourcePlan>();
        var taken = new HashSet<(LUID, uint)>();

        // Round 1: keep the source already driving a member (keeps its mode); never steal one another item claimed.
        foreach (var item in desired.Items)
        {
            var adapter = adapters[item];
            var current = all
                .Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && p.targetInfo.adapterId.Equals(adapter))
                .Where(p => item.Members.Any(m => targets[m].TargetId == p.targetInfo.id))
                .Select(p => p.sourceInfo.id)
                .Distinct()
                .Order();
            foreach (var sourceId in current)
            {
                if (taken.Contains((adapter, sourceId))) continue;
                if (PathsFrom(item, sourceId) is not { } pathTo) continue;
                assigned[item] = new SourcePlan(adapter, sourceId, pathTo);
                taken.Add((adapter, sourceId));
                break;
            }
        }

        // Round 2: any free source on the adapter that can reach every member.
        foreach (var item in desired.Items.Where(i => !assigned.ContainsKey(i)))
        {
            var adapter = adapters[item];
            var candidates = pathIndex.Keys
                .Where(k => k.Item1.Equals(adapter) && !taken.Contains((adapter, k.Item2)))
                .Select(k => k.Item2)
                .Distinct()
                .Order();
            foreach (var sourceId in candidates)
            {
                if (PathsFrom(item, sourceId) is not { } pathTo) continue;
                assigned[item] = new SourcePlan(adapter, sourceId, pathTo);
                taken.Add((adapter, sourceId));
                break;
            }
            if (!assigned.ContainsKey(item))
                throw new DisplayConfigException(
                    $"No free display source on the graphics adapter can drive {string.Join(" + ", item.Members)}.", 0);
        }

        return desired.Items.Select(i => (i, assigned[i])).ToList();
    }

    /// <summary>For each desired item, the index of the source mode now driving it.</summary>
    private static int[] LocateActiveSources(DisplayLayout desired, Config active)
    {
        var modeOf = new Dictionary<DisplayId, int>();
        foreach (var p in active.Paths)
        {
            if (p.sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID) continue;
            var name = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            if (name.DevicePath.Length > 0) modeOf[new DisplayId(name.DevicePath)] = (int)p.sourceInfo.modeInfoIdx;
        }

        return desired.Items.Select(item =>
            modeOf.TryGetValue(item.Members[0], out int idx)
                ? idx
                : throw new DisplayConfigException($"Windows did not enable Display '{item.Members[0]}'.", 0)).ToArray();
    }

    private static Dictionary<DisplayId, (LUID Adapter, uint TargetId)> MapTargets(DISPLAYCONFIG_PATH_INFO[] paths)
    {
        var map = new Dictionary<DisplayId, (LUID, uint)>();
        var visited = new HashSet<(LUID, uint)>();
        foreach (var p in paths)
        {
            var t = p.targetInfo;
            if (t.targetAvailable == 0 || !visited.Add((t.adapterId, t.id))) continue;
            var name = GetTargetName(t.adapterId, t.id);
            if (name.DevicePath.Length > 0) map.TryAdd(new DisplayId(name.DevicePath), (t.adapterId, t.id));
        }
        return map;
    }

    private static DisplayLayout ReadLayout(Config active)
    {
        var items = new List<LayoutItem>();
        foreach (var group in active.Paths
                     .Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0
                                 && p.sourceInfo.modeInfoIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                     .GroupBy(p => (p.sourceInfo.adapterId, p.sourceInfo.id)))
        {
            var members = group
                .Select(p => GetTargetName(p.targetInfo.adapterId, p.targetInfo.id).DevicePath)
                .Where(path => path.Length > 0)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .Select(path => new DisplayId(path))
                .ToImmutableArray();
            if (members.IsEmpty) continue;

            var mode = active.Modes[group.First().sourceInfo.modeInfoIdx].sourceMode;
            var bounds = new PixelRect(mode.position.x, mode.position.y, (int)mode.width, (int)mode.height);
            items.Add(new LayoutItem(members, bounds, bounds.X == 0 && bounds.Y == 0));
        }

        return new DisplayLayout(items.OrderBy(i => i.Bounds.X).ThenBy(i => i.Bounds.Y).ToImmutableArray());
    }

    private static PixelSize? CurrentSourceSize(Config all, DISPLAYCONFIG_PATH_INFO forTarget)
    {
        foreach (var p in all.Paths)
        {
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0
                || p.sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID
                || !p.targetInfo.adapterId.Equals(forTarget.targetInfo.adapterId)
                || p.targetInfo.id != forTarget.targetInfo.id) continue;
            var mode = all.Modes[p.sourceInfo.modeInfoIdx].sourceMode;
            return new PixelSize((int)mode.width, (int)mode.height);
        }
        return null;
    }

    // ---- raw CCD calls ----

    private sealed record Config(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes);

    private static unsafe Config Query(uint flags)
    {
        while (true)
        {
            int error = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (error != ERROR_SUCCESS)
                throw new DisplayConfigException($"GetDisplayConfigBufferSizes failed (error {error}).", error);

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                error = QueryDisplayConfig(flags, ref pathCount, p, ref modeCount, m, null);
            }
            // The configuration can change between the two calls; ask again for the new sizes.
            if (error == ERROR_INSUFFICIENT_BUFFER) continue;
            if (error != ERROR_SUCCESS)
                throw new DisplayConfigException($"QueryDisplayConfig failed (error {error}).", error);
            return new Config(paths[..(int)pathCount], modes[..(int)modeCount]);
        }
    }

    private static unsafe void Set(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, string what)
    {
        int error;
        fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
        {
            // An empty array pins to null, which is what SetDisplayConfig expects for "no modes".
            error = SetDisplayConfig((uint)paths.Length, p, (uint)modes.Length, m, ApplyFlags);
        }
        if (error != ERROR_SUCCESS)
            throw new DisplayConfigException($"{what} failed: SetDisplayConfig returned error {error}.", error);
    }

    private static unsafe (string FriendlyName, string DevicePath) GetTargetName(LUID adapter, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME), adapter, targetId),
        };
        int error = DisplayConfigGetDeviceInfo(&request.header);
        if (error != ERROR_SUCCESS)
            throw new DisplayConfigException($"Reading the name of display target {targetId} failed (error {error}).", error);
        return (FromBuffer(request.monitorFriendlyDeviceName, 64), FromBuffer(request.monitorDevicePath, 128));
    }

    private static unsafe string GetAdapterName(LUID adapter)
    {
        var request = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME, (uint)sizeof(DISPLAYCONFIG_ADAPTER_NAME), adapter, 0),
        };
        int error = DisplayConfigGetDeviceInfo(&request.header);
        if (error != ERROR_SUCCESS)
            throw new DisplayConfigException($"Reading the graphics adapter name failed (error {error}).", error);
        return FromBuffer(request.adapterDevicePath, 128);
    }

    private static unsafe PixelSize? GetPreferredSize(LUID adapter, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE, (uint)sizeof(DISPLAYCONFIG_TARGET_PREFERRED_MODE), adapter, targetId),
        };
        if (DisplayConfigGetDeviceInfo(&request.header) != ERROR_SUCCESS || request.width == 0 || request.height == 0)
            return null;
        return new PixelSize((int)request.width, (int)request.height);
    }

    private static unsafe string FromBuffer(char* buffer, int capacity)
    {
        var chars = new ReadOnlySpan<char>(buffer, capacity);
        int end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, uint size, LUID adapter, uint id) =>
        new() { type = type, size = size, adapterId = adapter, id = id };
}
