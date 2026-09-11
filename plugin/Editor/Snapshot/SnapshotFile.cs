using System;
using System.IO;

namespace Adanub.UnityMcp.Editor.Snapshot
{
    /// <summary>What a memory snapshot file on disk is, as far as a capture's completion goes.</summary>
    public enum SnapshotFileState
    {
        /// <summary>No file at the path.</summary>
        Missing,

        /// <summary>A file without a footer yet: still being written, or truncated.</summary>
        Incomplete,

        /// <summary>Header, footer, chapter directory and block section all check out.</summary>
        Complete,

        /// <summary>A file that is not a snapshot, or whose footer points at something that is not one.</summary>
        Invalid,
    }

    /// <summary>The verdict on one file: its state, its length, and what failed when it is not complete.</summary>
    public readonly struct SnapshotFileCheck
    {
        public readonly SnapshotFileState State;
        public readonly long Length;

        /// <summary>Why the file is not complete; null when it is.</summary>
        public readonly string Detail;

        public SnapshotFileCheck(SnapshotFileState state, long length, string detail)
        {
            State = state;
            Length = length;
            Detail = detail;
        }
    }

    /// <summary>
    /// The Unity memory snapshot container, as far as deciding whether a file is a finished capture.
    /// Engine-free (no Unity types): a request-thread route calls it without touching the editor, and
    /// a headless test can run it.
    ///
    /// Layout, as the Memory Profiler package's reader checks it: a uint header signature at offset 0;
    /// the last 4 bytes a uint footer signature and the 8 before them the chapter directory's address;
    /// at the directory a uint signature, a uint chapter-section version and the ulong address of the
    /// block section, which itself starts with a uint version. A file whose header is right but whose
    /// footer is not there yet reads as incomplete rather than invalid, since a capture in progress and
    /// a truncated file look the same from outside.
    /// </summary>
    public static class SnapshotFile
    {
        public const string Extension = ".snap";

        private const uint HeaderSignature = 0xAEABCDCD;
        private const uint DirectorySignature = 0xCDCDAEAB;
        private const uint FooterSignature = 0xABCDCDAE;

        // The chapter and block sections carry the same version number.
        private const uint SectionVersion = 0x20170724;

        // Header signature, directory address and footer signature: the least a finished file holds.
        private const long HeaderAndFooterLength = sizeof(uint) + sizeof(ulong) + sizeof(uint);

        // Directory signature, chapter-section version and block-section address.
        private const long DirectoryHeaderLength = sizeof(uint) + sizeof(uint) + sizeof(ulong);

        public static SnapshotFileCheck Check(string path)
        {
            if (!File.Exists(path))
                return new SnapshotFileCheck(SnapshotFileState.Missing, 0, "no file at the path");

            long length = 0;
            try
            {
                // Shared read: the writer may still hold the file open.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(stream);
                length = stream.Length;

                if (length < HeaderAndFooterLength)
                    return new SnapshotFileCheck(SnapshotFileState.Incomplete, length,
                        $"{length} bytes, shorter than a finished file's header and footer");

                uint header = reader.ReadUInt32();
                if (header != HeaderSignature)
                    return Invalid(length, $"header signature 0x{header:X8}, expected 0x{HeaderSignature:X8}: not a memory snapshot");

                stream.Position = length - sizeof(uint);
                uint footer = reader.ReadUInt32();
                if (footer != FooterSignature)
                    return new SnapshotFileCheck(SnapshotFileState.Incomplete, length,
                        $"footer signature 0x{footer:X8}, expected 0x{FooterSignature:X8}: still being written, or truncated");

                stream.Position = length - sizeof(uint) - sizeof(ulong);
                ulong directory = reader.ReadUInt64();
                if (directory == 0 || directory > (ulong)(length - HeaderAndFooterLength - DirectoryHeaderLength))
                    return Invalid(length, $"the footer's directory address {directory} lies outside the file's {length} bytes");

                stream.Position = (long)directory;
                uint directorySignature = reader.ReadUInt32();
                if (directorySignature != DirectorySignature)
                    return Invalid(length, $"directory signature 0x{directorySignature:X8} at {directory}, expected 0x{DirectorySignature:X8}");
                uint chapterVersion = reader.ReadUInt32();
                if (chapterVersion != SectionVersion)
                    return Invalid(length, $"chapter-section version 0x{chapterVersion:X8}, expected 0x{SectionVersion:X8}");

                ulong blocks = reader.ReadUInt64();
                if (blocks == 0 || blocks > (ulong)(length - sizeof(uint)))
                    return Invalid(length, $"the directory's block-section address {blocks} lies outside the file's {length} bytes");
                stream.Position = (long)blocks;
                uint blockVersion = reader.ReadUInt32();
                if (blockVersion != SectionVersion)
                    return Invalid(length, $"block-section version 0x{blockVersion:X8} at {blocks}, expected 0x{SectionVersion:X8}");

                return new SnapshotFileCheck(SnapshotFileState.Complete, length, null);
            }
            catch (IOException ex)
            {
                // A sharing violation while the capture holds the file is the expected case.
                return new SnapshotFileCheck(SnapshotFileState.Incomplete, length, $"unreadable for now: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return Invalid(length, $"access denied: {ex.Message}");
            }
        }

        private static SnapshotFileCheck Invalid(long length, string detail) =>
            new SnapshotFileCheck(SnapshotFileState.Invalid, length, detail);
    }
}
