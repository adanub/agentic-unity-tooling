namespace Adanub.UnityMcp.Editor.Snapshot
{
    /// <summary>
    /// The snapshot chapters this tool reads, by their index in the file's chapter directory. The
    /// indices are fixed by the snapshot format (they follow the order of the Memory Profiler package's
    /// entry types); only the chapters the tool needs are named. The graphics-resource and allocator
    /// chapters exist from format version 14; before format version 18 an instance ID is 4 bytes, from
    /// then on 8.
    /// </summary>
    public enum SnapshotChapter
    {
        FormatVersion = 0,
        CaptureFlags = 3,

        NativeTypeNames = 5,

        NativeObjectTypeIndices = 7,
        NativeObjectInstanceIds = 10,
        NativeObjectNames = 11,
        NativeObjectSizes = 13,
        NativeObjectRootIds = 14,

        NativeRootIds = 35,
        NativeRootAreaNames = 36,
        NativeRootObjectNames = 37,
        NativeRootAccumulatedSizes = 38,

        NativeAllocationRootIds = 40,
        NativeAllocationSizes = 43,

        GraphicsResourceIds = 70,
        GraphicsResourceSizes = 71,
        GraphicsResourceRootIds = 72,

        AllocatorNames = 73,
        AllocatorUsedSizes = 75,
        AllocatorReservedSizes = 76,
        AllocatorAllocationCounts = 79,
    }
}
