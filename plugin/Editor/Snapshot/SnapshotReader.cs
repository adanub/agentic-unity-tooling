using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Adanub.UnityMcp.Editor.Snapshot
{
    /// <summary>A snapshot file that is not what the reader needs; the message names the chapter, address or value that failed.</summary>
    public sealed class SnapshotFormatException : Exception
    {
        public SnapshotFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Reads chapters out of a Unity memory snapshot. Engine-free (no Unity types): a headless test runs
    /// it, and a request-thread route can call it without touching the editor.
    ///
    /// The container: the footer points at a chapter directory, which lists one file address per chapter
    /// (0 = absent) and the address of the block section, which lists one file address per block. A
    /// chapter starts with an 18-byte header — format (ushort), block index (uint), then a uint and a
    /// ulong whose meaning depends on the format — and its data lives in a BLOCK: a logical byte range
    /// stored as fixed-size chunks scattered through the file (the block's header gives the chunk size
    /// and the total length, then one file address per chunk). The three formats:
    /// <list type="bullet">
    /// <item>a single element: its size in the uint, its block offset in the ulong;</item>
    /// <item>an array of equal-size elements from block offset 0: element size in the uint, count in the
    /// LOW 32 bits of the ulong (the high half carries other data - 0x00170002 in a real capture);</item>
    /// <item>an array of variable-size elements: count in the uint; the header's ulong holds the first
    /// element's block offset, and the table after the header holds the others' offsets followed by the
    /// total length.</item>
    /// </list>
    /// </summary>
    public sealed class SnapshotReader : IDisposable
    {
        private const ushort UnwrittenFormat = 0;
        private const ushort SingleElement = 1;
        private const ushort ConstantSizeArray = 2;
        private const ushort DynamicSizeArray = 3;

        private const int ChapterHeaderLength = sizeof(ushort) + sizeof(uint) + sizeof(uint) + sizeof(ulong);
        private const int BlockHeaderLength = sizeof(ulong) + sizeof(ulong);

        // Format version from which an instance ID is an 8-byte entity ID rather than a 4-byte int.
        private const uint EightByteInstanceIdsVersion = 18;

        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private readonly long[] _chapterAddresses;
        private readonly long[] _blockAddresses;
        private readonly Dictionary<uint, BlockTable> _blocks = new Dictionary<uint, BlockTable>();

        public string FilePath { get; }
        public long Length { get; }
        public uint FormatVersion { get; }

        /// <summary>How many chapter slots the file's directory lists (the format's entry-type count when it was written).</summary>
        public int ChapterSlots => _chapterAddresses.Length;

        private sealed class BlockTable
        {
            public ulong ChunkSize;
            public ulong TotalBytes;
            public long[] Chunks;
        }

        private struct ChapterHeader
        {
            public long Address;
            public ushort Format;
            public uint Block;
            public uint UintMeta;
            public ulong UlongMeta;
        }

        /// <summary>Opens a finished snapshot; throws <see cref="SnapshotFormatException"/> for anything else, with the reason.</summary>
        public static SnapshotReader Open(string path)
        {
            var check = SnapshotFile.Check(path);
            if (check.State != SnapshotFileState.Complete)
                throw new SnapshotFormatException($"{path} is {check.State.ToString().ToLowerInvariant()}: {check.Detail}");
            if (!BitConverter.IsLittleEndian)
                throw new SnapshotFormatException("snapshots are little-endian; this reader runs only on a little-endian host");
            return new SnapshotReader(path);
        }

        private SnapshotReader(string path)
        {
            FilePath = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _reader = new BinaryReader(_stream);
            try
            {
                Length = _stream.Length;

                // SnapshotFile.Check has verified the footer, the directory's signature and version, and
                // the block section's version; what follows reads the tables they frame.
                _stream.Position = Length - sizeof(uint) - sizeof(ulong);
                long directory = (long)_reader.ReadUInt64();
                _stream.Position = directory + sizeof(uint) + sizeof(uint);
                long blockSection = (long)_reader.ReadUInt64();

                int chapterCount = _reader.ReadInt32();
                if (chapterCount < 0)
                    throw new SnapshotFormatException($"the chapter directory at {directory} lists {chapterCount} chapters");
                RequireInFile(_stream.Position, (long)chapterCount * sizeof(long), "the chapter directory's address table");
                _chapterAddresses = ReadInt64s(chapterCount);

                _stream.Position = blockSection + sizeof(uint);
                int blockCount = _reader.ReadInt32();
                if (blockCount < 1)
                    throw new SnapshotFormatException($"the block section at {blockSection} lists {blockCount} blocks");
                RequireInFile(_stream.Position, (long)blockCount * sizeof(long), "the block section's address table");
                _blockAddresses = ReadInt64s(blockCount);

                FormatVersion = Has(SnapshotChapter.FormatVersion)
                    ? BitConverter.ToUInt32(ReadSingle(SnapshotChapter.FormatVersion, sizeof(uint)), 0)
                    : 0;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _stream?.Dispose();
        }

        /// <summary>Whether the file holds the chapter (an older format lacks the newer ones).</summary>
        public bool Has(SnapshotChapter chapter)
        {
            int index = (int)chapter;
            return index < _chapterAddresses.Length && _chapterAddresses[index] != 0;
        }

        /// <summary>The chapter's element count; 0 when the file does not hold it.</summary>
        public long Count(SnapshotChapter chapter)
        {
            if (!Has(chapter))
                return 0;
            var header = Header(chapter);
            switch (header.Format)
            {
                case SingleElement:
                    return 1;
                case ConstantSizeArray:
                    return ConstantCount(header);
                case DynamicSizeArray:
                    return header.UintMeta;
                default:
                    return 0;
            }
        }

        public ulong[] ReadUInt64s(SnapshotChapter chapter)
        {
            byte[] bytes = ReadConstant(chapter, sizeof(ulong), out long count);
            var values = new ulong[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        public long[] ReadInt64s(SnapshotChapter chapter)
        {
            byte[] bytes = ReadConstant(chapter, sizeof(long), out long count);
            var values = new long[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        public int[] ReadInt32s(SnapshotChapter chapter)
        {
            byte[] bytes = ReadConstant(chapter, sizeof(int), out long count);
            var values = new int[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        /// <summary>The native objects' instance IDs, widened to 8 bytes whatever the format version stores.</summary>
        public long[] ReadInstanceIds()
        {
            if (FormatVersion >= EightByteInstanceIdsVersion)
                return ReadInt64s(SnapshotChapter.NativeObjectInstanceIds);
            int[] narrow = ReadInt32s(SnapshotChapter.NativeObjectInstanceIds);
            var wide = new long[narrow.Length];
            for (int i = 0; i < narrow.Length; i++)
                wide[i] = narrow[i];
            return wide;
        }

        /// <summary>A chapter of variable-size UTF-8 strings (names).</summary>
        public string[] ReadStrings(SnapshotChapter chapter)
        {
            var header = Header(chapter);
            if (header.Format == UnwrittenFormat)
                return Array.Empty<string>();
            if (header.Format != DynamicSizeArray)
                throw new SnapshotFormatException($"chapter {chapter} has format {header.Format}; strings are a variable-size array ({DynamicSizeArray})");

            long count = header.UintMeta;
            if (count == 0)
                return Array.Empty<string>();

            // On disk the header's ulong holds element 0's offset and the table holds elements 1..n-1,
            // then the total length; rotated here into one start offset per element plus the end.
            RequireInFile(header.Address + ChapterHeaderLength, count * sizeof(long), $"chapter {chapter}'s offset table");
            _stream.Position = header.Address + ChapterHeaderLength;
            long[] stored = ReadInt64s(checked((int)count));
            var offsets = new long[count + 1];
            offsets[0] = (long)header.UlongMeta;
            Array.Copy(stored, 0, offsets, 1, count);

            for (long i = 0; i < count; i++)
            {
                if (offsets[i + 1] < offsets[i])
                    throw new SnapshotFormatException($"chapter {chapter}: element {i} ends at block offset {offsets[i + 1]}, before it starts at {offsets[i]}");
            }

            var block = Block(header.Block, chapter);
            byte[] bytes = ReadBlock(block, (ulong)offsets[0], offsets[count] - offsets[0], chapter);
            var values = new string[count];
            for (long i = 0; i < count; i++)
            {
                int start = checked((int)(offsets[i] - offsets[0]));
                int length = checked((int)(offsets[i + 1] - offsets[i]));
                values[i] = Encoding.UTF8.GetString(bytes, start, length).TrimEnd('\0');
            }
            return values;
        }

        private byte[] ReadSingle(SnapshotChapter chapter, int expectedSize)
        {
            var header = Header(chapter);
            if (header.Format != SingleElement)
                throw new SnapshotFormatException($"chapter {chapter} has format {header.Format}; a single element ({SingleElement}) was expected");
            if (header.UintMeta != expectedSize)
                throw new SnapshotFormatException($"chapter {chapter} holds {header.UintMeta} bytes; {expectedSize} were expected");
            return ReadBlock(Block(header.Block, chapter), header.UlongMeta, expectedSize, chapter);
        }

        private byte[] ReadConstant(SnapshotChapter chapter, int elementSize, out long count)
        {
            var header = Header(chapter);
            count = 0;
            if (header.Format == UnwrittenFormat)
                return Array.Empty<byte>();
            if (header.Format != ConstantSizeArray)
                throw new SnapshotFormatException($"chapter {chapter} has format {header.Format}; an array of equal-size elements ({ConstantSizeArray}) was expected");
            if (header.UintMeta != elementSize)
                throw new SnapshotFormatException($"chapter {chapter} stores elements of {header.UintMeta} bytes; this read expects {elementSize}");

            count = ConstantCount(header);
            return ReadBlock(Block(header.Block, chapter), 0, count * elementSize, chapter);
        }

        // An equal-size array's count is the low 32 bits of the header's ulong.
        private static long ConstantCount(ChapterHeader header) => (uint)header.UlongMeta;

        private ChapterHeader Header(SnapshotChapter chapter)
        {
            if (!Has(chapter))
                throw new SnapshotFormatException(
                    $"chapter {chapter} (slot {(int)chapter}) is absent from this snapshot (format version {FormatVersion}, {_chapterAddresses.Length} slots)");

            long address = _chapterAddresses[(int)chapter];
            RequireInFile(address, ChapterHeaderLength, $"chapter {chapter}'s header");
            _stream.Position = address;
            return new ChapterHeader
            {
                Address = address,
                Format = _reader.ReadUInt16(),
                Block = _reader.ReadUInt32(),
                UintMeta = _reader.ReadUInt32(),
                UlongMeta = _reader.ReadUInt64(),
            };
        }

        private BlockTable Block(uint index, SnapshotChapter chapter)
        {
            if (_blocks.TryGetValue(index, out var table))
                return table;
            if (index >= _blockAddresses.Length)
                throw new SnapshotFormatException($"chapter {chapter} names block {index}; the file has {_blockAddresses.Length}");

            long address = _blockAddresses[index];
            RequireInFile(address, BlockHeaderLength, $"block {index}'s header");
            _stream.Position = address;
            ulong chunkSize = _reader.ReadUInt64();
            ulong totalBytes = _reader.ReadUInt64();
            if (chunkSize == 0)
                throw new SnapshotFormatException($"block {index} declares a chunk size of 0");

            ulong chunkCount = totalBytes / chunkSize + (totalBytes % chunkSize != 0 ? 1UL : 0UL);
            RequireInFile(address + BlockHeaderLength, (long)chunkCount * sizeof(long), $"block {index}'s chunk table");
            table = new BlockTable { ChunkSize = chunkSize, TotalBytes = totalBytes, Chunks = ReadInt64s(checked((int)chunkCount)) };
            _blocks[index] = table;
            return table;
        }

        // Bytes [offset, offset + length) of a block, gathered from its scattered chunks.
        private byte[] ReadBlock(BlockTable block, ulong offset, long length, SnapshotChapter chapter)
        {
            if (length < 0 || offset + (ulong)length > block.TotalBytes)
                throw new SnapshotFormatException($"chapter {chapter} reads {length} bytes at block offset {offset}; its block holds {block.TotalBytes}");
            if (length > int.MaxValue)
                throw new SnapshotFormatException($"chapter {chapter} is {length} bytes, more than one read can hold");

            var bytes = new byte[length];
            long done = 0;
            while (done < length)
            {
                ulong position = offset + (ulong)done;
                long chunk = (long)(position / block.ChunkSize);
                long within = (long)(position % block.ChunkSize);
                long take = Math.Min(length - done, (long)block.ChunkSize - within);
                long filePosition = block.Chunks[chunk] + within;
                RequireInFile(filePosition, take, $"chapter {chapter}'s chunk {chunk}");
                _stream.Position = filePosition;
                ReadExactly(bytes, done, take);
                done += take;
            }
            return bytes;
        }

        private long[] ReadInt64s(int count)
        {
            var bytes = new byte[(long)count * sizeof(long)];
            ReadExactly(bytes, 0, bytes.Length);
            var values = new long[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        private void ReadExactly(byte[] into, long offset, long count)
        {
            while (count > 0)
            {
                int read = _stream.Read(into, checked((int)offset), checked((int)Math.Min(count, int.MaxValue)));
                if (read <= 0)
                    throw new SnapshotFormatException($"the file ended at {_stream.Position} with {count} bytes still to read");
                offset += read;
                count -= read;
            }
        }

        private void RequireInFile(long position, long length, string what)
        {
            if (position < 0 || length < 0 || position > Length - length)
                throw new SnapshotFormatException($"{what} at {position} (+{length} bytes) lies outside the file's {Length} bytes");
        }
    }
}
