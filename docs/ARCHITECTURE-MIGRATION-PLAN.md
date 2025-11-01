# Architecture Migration Plan: Managed Fallback with Native Fast-Path

## Executive Summary

**Goal:** Implement pure managed C# implementations of all archive functionality with feature parity, using native libarchive as an optional fast-path when available.

**Current State:** Heavy reliance on high-level libarchive C APIs - failure to load native library = complete failure

**Target State:**
- **Primary:** Pure managed C# implementation (100% cross-platform, no P/Invoke required)
- **Fast-path:** Native libarchive acceleration when available (2-10x faster)
- **Graceful degradation:** If native library fails to load, everything still works via managed code

**Key Principle:** Native is an optimization, not a requirement

---

## 1. Current Architecture Analysis

### Current Dependency Stack
```
┌─────────────────────────────────┐
│  C# Application Layer           │
│  (LibArchiveReader/Writer)      │
├─────────────────────────────────┤
│  P/Invoke Boundary              │
├─────────────────────────────────┤
│  libarchive.so/dll/dylib        │
│  (Monolithic: ~2-3 MB)          │
│   ├─ Archive Format Handlers   │
│   ├─ Compression (zlib, lz4...) │
│   ├─ Encryption                 │
│   └─ Entry Management           │
└─────────────────────────────────┘
```

### Current Strengths
✅ Single native dependency (simple distribution)
✅ Comprehensive format support (30+ formats)
✅ Battle-tested, mature library
✅ Active upstream development

### Current Limitations
❌ Limited control over individual stages
❌ Opaque error handling
❌ All-or-nothing native calls
❌ Cannot optimize specific workflows
❌ Difficult to add C#-specific features

---

## 2. Proposed Architecture: Facade with Managed Fallback

### Target Architecture
```
┌──────────────────────────────────────────────────────────┐
│  Public API (LibArchiveReader/Writer)                    │
│  - Same API regardless of implementation                 │
│  - User doesn't know if native or managed                │
└─────────────────┬────────────────────────────────────────┘
                  │
                  ├─► Try: Native Fast-Path (if available)
                  │   ├─ Load libarchive.so/dll/dylib
                  │   ├─ P/Invoke to native functions
                  │   └─ 2-10x faster performance
                  │
                  └─► Fallback: Pure Managed C# Implementation
                      ├─ No P/Invoke required
                      ├─ 100% cross-platform
                      ├─ AOT friendly
                      └─ Full feature parity
```

### Detailed Managed Implementation Stack
```
┌─────────────────────────────────────────────┐
│  LibArchive.Net.Managed                     │
│  ├─ Readers (IArchiveReader)                │
│  │   ├─ ZipReader (System.IO.Compression)   │
│  │   ├─ TarReader (custom implementation)   │
│  │   ├─ SevenZipReader (SharpCompress?)     │
│  │   └─ GzipReader (System.IO.Compression)  │
│  ├─ Writers (IArchiveWriter)                │
│  │   ├─ ZipWriter (System.IO.Compression)   │
│  │   ├─ TarWriter (custom implementation)   │
│  │   └─ GzipWriter (System.IO.Compression)  │
│  ├─ Compression (ICompressor)               │
│  │   ├─ Deflate (System.IO.Compression)     │
│  │   ├─ Brotli (System.IO.Compression)      │
│  │   ├─ LZ4 (K4os.Compression.LZ4)          │
│  │   ├─ Zstd (ZstdSharp)                    │
│  │   └─ LZMA (Managed LZMA SDK)             │
│  └─ Utilities                               │
│      ├─ Format Detection                    │
│      ├─ Path Validation                     │
│      └─ Entry Metadata                      │
└─────────────────────────────────────────────┘
```

---

## 3. Migration Strategy: Managed-First with Native Acceleration

### Overall Strategy

**Guiding Principle:** Every feature implemented in pure C# first, then optionally accelerated with native code.

**Implementation Pattern:**
```csharp
public class LibArchiveReader
{
    private IArchiveReaderImplementation impl;

    public LibArchiveReader(string path)
    {
        // Try native first (fast path)
        if (NativeLibraryLoader.TryLoad(out var nativeImpl))
        {
            impl = new NativeArchiveReader(path, nativeImpl);
        }
        else
        {
            // Fallback to managed (slower but works everywhere)
            impl = CreateManagedReader(path);
        }
    }

    private IArchiveReaderImplementation CreateManagedReader(string path)
    {
        var format = FormatDetector.Detect(path);
        return format switch
        {
            ArchiveFormat.Zip => new ManagedZipReader(path),
            ArchiveFormat.Tar => new ManagedTarReader(path),
            ArchiveFormat.Gzip => new ManagedGzipReader(path),
            // ... more formats
            _ => throw new NotSupportedException($"Format {format} not supported in managed mode")
        };
    }
}
```

---

### Phase 1: Facade Pattern + Managed ZIP/TAR (3-6 months)
**Risk:** ⚡ Low | **Effort:** 🔨 Medium | **Value:** 💎 Very High

**Objective:** Implement facade pattern with managed ZIP and TAR support as proof of concept

#### 3.1.1 Format Detection
```csharp
public static class ArchiveFormatDetector
{
    private static readonly Dictionary<byte[], ArchiveFormat> MagicBytes = new()
    {
        { new byte[] { 0x50, 0x4B, 0x03, 0x04 }, ArchiveFormat.Zip },
        { new byte[] { 0x37, 0x7A, 0xBC, 0xAF }, ArchiveFormat.SevenZip },
        { new byte[] { 0x1F, 0x8B }, ArchiveFormat.Gzip },
        { new byte[] { 0x42, 0x5A, 0x68 }, ArchiveFormat.Bzip2 },
        // TAR has no magic bytes, check ustar signature at offset 257
    };

    public static ArchiveFormat? Detect(Stream stream)
    {
        var buffer = new byte[16];
        var originalPosition = stream.Position;

        try
        {
            stream.Read(buffer, 0, 16);

            foreach (var (magic, format) in MagicBytes)
            {
                if (buffer.Take(magic.Length).SequenceEqual(magic))
                    return format;
            }

            // Check TAR ustar signature
            if (stream.Length > 257)
            {
                stream.Position = 257;
                stream.Read(buffer, 0, 6);
                if (Encoding.ASCII.GetString(buffer, 0, 5) == "ustar")
                    return ArchiveFormat.Tar;
            }

            return null;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}
```

**Benefits:**
- No P/Invoke overhead for detection
- Auto-detect format from extension or content
- Better user experience

#### 3.1.2 Path Validation & Security
```csharp
public static class PathValidator
{
    public static string NormalizePath(string path)
    {
        // Convert backslashes to forward slashes
        path = path.Replace('\\', '/');

        // Remove redundant slashes
        path = Regex.Replace(path, "/+", "/");

        // Remove leading slash for relative paths
        path = path.TrimStart('/');

        return path;
    }

    public static bool IsSecurePath(string path)
    {
        // Prevent path traversal attacks
        if (path.Contains(".."))
            return false;

        if (path.Contains("//"))
            return false;

        // Prevent absolute paths
        if (Path.IsPathRooted(path))
            return false;

        return true;
    }

    public static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty");

        if (!IsSecurePath(path))
            throw new SecurityException($"Unsafe path detected: {path}");
    }
}
```

**Benefits:**
- Security: Prevent path traversal attacks
- Consistency: Platform-independent path handling
- Better error messages

#### 3.1.3 Enhanced Progress Reporting
```csharp
public class AdvancedProgressTracker : IProgress<FileProgress>
{
    private readonly IProgress<EnhancedProgress> target;
    private readonly Queue<(DateTime, long)> recentSpeeds = new();
    private DateTime startTime;
    private long totalBytesProcessed;

    public void Report(FileProgress progress)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - startTime;

        // Calculate rolling average speed
        var currentSpeed = CalculateSpeed(progress.BytesProcessed);

        // Estimate time remaining
        var bytesRemaining = progress.TotalBytes - progress.BytesProcessed;
        var eta = TimeSpan.FromSeconds(bytesRemaining / currentSpeed);

        target.Report(new EnhancedProgress
        {
            FileIndex = progress.FileIndex,
            TotalFiles = progress.TotalFiles,
            FilePath = progress.FilePath,
            PercentComplete = progress.PercentComplete,
            BytesProcessed = progress.BytesProcessed,
            TotalBytes = progress.TotalBytes,
            BytesPerSecond = currentSpeed,
            EstimatedTimeRemaining = eta,
            ElapsedTime = elapsed
        });
    }
}
```

**Benefits:**
- Better user feedback with ETA
- Smooth progress updates
- Speed monitoring

#### 3.1.4 Archive Validation
```csharp
public static class ArchiveValidator
{
    public static ValidationResult Validate(string archivePath)
    {
        var result = new ValidationResult();

        using var reader = new LibArchiveReader(archivePath);

        foreach (var entry in reader.Entries())
        {
            // Check for suspicious patterns
            if (!PathValidator.IsSecurePath(entry.Name))
                result.AddWarning($"Potentially unsafe path: {entry.Name}");

            // Check for zip bombs
            if (entry.Size > 1_000_000_000 && entry.CompressedSize < 1_000_000)
                result.AddWarning($"High compression ratio (potential zip bomb): {entry.Name}");

            // Check for encrypted entries without password
            if (entry.IsEncrypted && !reader.HasPassword)
                result.AddError($"Encrypted entry requires password: {entry.Name}");
        }

        return result;
    }
}
```

**Phase 1 Deliverables:**
- [ ] Format detection from magic bytes
- [ ] Path validation and normalization
- [ ] Enhanced progress reporting with ETA
- [ ] Archive validation utilities
- [ ] Security analysis helpers

**Estimated Effort:** 2-3 weeks
**No native code changes required** ✅

---

### Phase 2: Lower-Level libarchive APIs (3-12 months)
**Risk:** ⚡ Medium | **Effort:** 🔨 Medium-High | **Value:** 💎 High

**Objective:** Use lower-level libarchive APIs for finer control

#### 3.2.1 Current vs Lower-Level API Comparison

**Current (High-Level):**
```csharp
// Read entire entry in one call
var buffer = new byte[entry.Size];
archive_read_data(handle, buffer, buffer.Length);
```

**Proposed (Lower-Level):**
```csharp
// Block-by-block reading with finer control
while (true)
{
    var result = archive_read_data_block(
        handle,
        out IntPtr buffPtr,
        out size_t size,
        out long offset);

    if (result == ARCHIVE_EOF) break;

    // Process block in C# (hash, filter, transform)
    ProcessBlock(buffPtr, size, offset);
}
```

**Benefits:**
- Stream processing without allocating full entry size
- Ability to cancel mid-entry
- Better memory management
- Can implement custom filters in C#

#### 3.2.2 Direct Entry Manipulation

**Current:**
```csharp
// Opaque entry handling
var name = archive_entry_pathname(entry);
var size = archive_entry_size(entry);
```

**Proposed:**
```csharp
// Create and manipulate entries in C#
var entry = new ArchiveEntryBuilder()
    .WithPath("data/file.txt")
    .WithSize(data.Length)
    .WithModificationTime(DateTime.UtcNow)
    .WithPermissions(0644)
    .Build();

// Convert to native only when needed
var nativeEntry = entry.ToNativeEntry();
archive_write_header(archive, nativeEntry);
```

**Benefits:**
- Type-safe entry construction
- Validation before native call
- Better error messages
- Easier testing

#### 3.2.3 Custom Stream Adapters

```csharp
public class ManagedStreamAdapter
{
    // Implement libarchive callbacks in C#
    private static archive_read_callback ReadCallback = (archive, client, buffer) =>
    {
        var stream = GCHandle.FromIntPtr(client).Target as Stream;
        var span = new Span<byte>((void*)buffer, BUFFER_SIZE);
        return stream.Read(span);
    };

    public static void AttachStream(IntPtr archive, Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        archive_read_set_read_callback(archive, ReadCallback);
        archive_read_set_callback_data(archive, GCHandle.ToIntPtr(handle));
    }
}
```

**Phase 2 Deliverables:**
- [ ] Block-by-block reading API
- [ ] Entry builder pattern
- [ ] Custom stream adapters
- [ ] Chunked writing support
- [ ] Cancel/pause support
- [ ] Memory-mapped file integration

**Estimated Effort:** 3-6 months
**Requires P/Invoke changes** ⚠️

---

### Phase 3: Individual Compression Libraries (12+ months, Optional)
**Risk:** ⚡ High | **Effort:** 🔨 Very High | **Value:** 💎 Variable

**Objective:** Optionally use individual compression libraries for specific use cases

#### 3.3.1 Use Case Analysis

| Library | Keep in libarchive? | Extract? | Rationale |
|---------|---------------------|----------|-----------|
| **zlib** | Optional | ✅ Consider | Common, good .NET wrappers, System.IO.Compression |
| **lz4** | Optional | ✅ Consider | Very fast, simple API, good for streaming |
| **zstd** | Optional | ✅ Consider | Modern, excellent perf, growing adoption |
| **bzip2** | ✅ Yes | ❌ No | Legacy, rarely used |
| **lzma/xz** | ✅ Yes | ❌ No | Complex, libarchive handles well |
| **lzo** | ✅ Yes | ❌ No | Rarely used |

#### 3.3.2 Example: Direct zstd Usage

**Scenario:** Large file compression where zstd-specific tuning needed

```csharp
public class ZstdCompressor
{
    [DllImport("libzstd")]
    private static extern nuint ZSTD_compress(
        IntPtr dst, nuint dstCapacity,
        IntPtr src, nuint srcSize,
        int compressionLevel);

    public byte[] Compress(byte[] data, int level = 3)
    {
        var maxCompressedSize = ZSTD_compressBound(data.Length);
        var compressed = new byte[maxCompressedSize];

        fixed (byte* srcPtr = data)
        fixed (byte* dstPtr = compressed)
        {
            var size = ZSTD_compress(
                (IntPtr)dstPtr, maxCompressedSize,
                (IntPtr)srcPtr, data.Length,
                level);

            if (ZSTD_isError(size))
                throw new Exception("Compression failed");

            Array.Resize(ref compressed, (int)size);
            return compressed;
        }
    }
}
```

**When to use direct compression:**
- Need specific compression parameters not exposed by libarchive
- Streaming compression with custom dictionaries
- Compression-only scenarios (no archive format)
- Performance-critical paths

#### 3.3.3 Hybrid Approach

```csharp
public class HybridArchiveWriter : IDisposable
{
    private readonly CompressionStrategy strategy;

    public void AddEntry(string path, byte[] data)
    {
        if (strategy == CompressionStrategy.Native)
        {
            // Use libarchive for everything
            LibArchiveWriter.AddEntry(path, data);
        }
        else if (strategy == CompressionStrategy.Managed)
        {
            // Compress in C#, then add to archive
            var compressed = ZstdCompressor.Compress(data);
            LibArchiveWriter.AddPrecompressedEntry(path, compressed);
        }
    }
}
```

**Phase 3 Deliverables (Optional):**
- [ ] zstd wrapper for streaming
- [ ] lz4 wrapper for fast compression
- [ ] Hybrid compression strategy
- [ ] Benchmark comparison tools
- [ ] Performance analysis documentation

**Estimated Effort:** 6-12 months
**Only pursue if specific needs identified** ⚠️

---

## 4. Alternative: Leverage Existing .NET Libraries

### Option A: System.IO.Compression for ZIP
```csharp
// For simple ZIP operations, use built-in .NET
using (var archive = ZipFile.Open("archive.zip", ZipArchiveMode.Create))
{
    archive.CreateEntryFromFile("source.txt", "archived.txt");
}

// Fall back to libarchive for:
// - AES encryption
// - Advanced options
// - Other formats
```

**Pros:**
- No native dependency for ZIP
- Native AOT friendly
- Well-tested, maintained by Microsoft

**Cons:**
- Limited format support (ZIP only)
- No encryption support
- Less control over compression

### Option B: SharpCompress
```csharp
// Alternative: Use SharpCompress for managed implementation
using SharpCompress.Archives;
using SharpCompress.Common;

// Pure C# implementation of multiple formats
// Consider for simple scenarios
```

**Evaluation:**
- [ ] Benchmark SharpCompress vs libarchive
- [ ] Evaluate feature parity
- [ ] Test Native AOT compatibility
- [ ] Consider as secondary option

---

## 5. Performance Considerations

### 5.1 P/Invoke Overhead Analysis

**Current:** Multiple P/Invoke calls per entry
```csharp
archive_read_next_header(archive, out entry);  // P/Invoke 1
var name = archive_entry_pathname(entry);       // P/Invoke 2
var size = archive_entry_size(entry);           // P/Invoke 3
var time = archive_entry_mtime(entry);          // P/Invoke 4
archive_read_data(archive, buffer, size);       // P/Invoke 5
```

**Optimized:** Single P/Invoke with struct marshalling
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct ArchiveEntryData
{
    public IntPtr pathname;
    public long size;
    public long mtime;
    // ... other fields
}

// Single P/Invoke gets all metadata
archive_entry_get_all_metadata(entry, out ArchiveEntryData data);
```

**Estimated improvement:** 2-3x faster metadata access

### 5.2 Memory Management

**Current:** Large allocations for full entries
```csharp
var buffer = new byte[entry.Size]; // Could be GB
archive_read_data(archive, buffer, buffer.Length);
```

**Optimized:** Streaming with bounded memory
```csharp
const int CHUNK_SIZE = 1 << 20; // 1 MB chunks
var buffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);

while (archive_read_data_block(/* ... */))
{
    // Process in chunks
    // Memory usage: O(CHUNK_SIZE) not O(entry.Size)
}
```

**Estimated improvement:** Constant memory usage, no LOH allocations

### 5.3 Benchmark Targets

| Operation | Current | Phase 1 Target | Phase 2 Target |
|-----------|---------|----------------|----------------|
| Format detection | ~50 μs | ~5 μs | ~5 μs |
| Entry enumeration | ~100 μs/entry | ~100 μs | ~50 μs |
| Small file read (<1MB) | ~1 ms | ~1 ms | ~0.5 ms |
| Large file read (>100MB) | ~500 ms | ~500 ms | ~300 ms |

---

## 6. Risk Mitigation

### 6.1 Compatibility Testing Matrix

Test each phase on:
- [ ] Windows (x86, x64, ARM64)
- [ ] Linux (x64, ARM, ARM64)
- [ ] macOS (x64, ARM64)
- [ ] .NET Standard 2.0, .NET 6, 8, 9
- [ ] Native AOT

### 6.2 Fallback Strategy

```csharp
public static class FeatureFlags
{
    // Feature flags for gradual rollout
    public static bool UseManagedFormatDetection { get; set; } = true;
    public static bool UseLowerLevelApis { get; set; } = false;
    public static bool UseDirectCompression { get; set; } = false;
}

// Always have native fallback
if (FeatureFlags.UseManagedFormatDetection)
{
    format = ArchiveFormatDetector.Detect(stream);
}
else
{
    // Fall back to libarchive detection
    format = LibArchiveNative.DetectFormat(stream);
}
```

### 6.3 Regression Testing

- [ ] Comprehensive test suite for Phase 1 before Phase 2
- [ ] Performance benchmarks tracked in CI
- [ ] Memory profiling for each change
- [ ] No regressions in existing functionality

---

## 7. Decision Framework

### When to Move to C#
✅ **YES** if:
- Pure computation (no I/O)
- Better error handling needed
- Type safety valuable
- Testability important
- No performance penalty

❌ **NO** if:
- Performance-critical tight loop
- Complex native data structures
- Well-optimized native implementation
- Limited maintainability gain

### When to Use Lower-Level APIs
✅ **YES** if:
- Need finer control
- Streaming/chunked processing
- Custom error handling
- Integration with C# features

❌ **NO** if:
- Current high-level API sufficient
- Complexity not justified
- Performance gain minimal

### When to Use Individual Compression Libraries
✅ **YES** if:
- Specific library features needed
- Measured performance gain
- Compression-only scenarios
- Custom dictionary support

❌ **NO** if:
- libarchive sufficient
- Added complexity not justified
- Marginal performance gain
- More native dependencies to maintain

---

## 8. Recommended Implementation Order

### Year 1: Foundation
1. ✅ **Q1:** Phase 1 - C# Utilities Layer
   - Format detection
   - Path validation
   - Enhanced progress

2. 🔄 **Q2-Q3:** Phase 2 Start - Lower-Level APIs
   - Block-by-block reading
   - Entry builders
   - Streaming support

3. 📊 **Q4:** Evaluation
   - Performance analysis
   - User feedback
   - Decide on Phase 3

### Year 2: Optimization (Conditional)
4. ⚠️ **Q1-Q2:** Phase 3 (if justified)
   - Individual compression libraries
   - Benchmarking
   - Hybrid strategies

5. 🎯 **Q3-Q4:** Polish
   - Performance tuning
   - Documentation
   - Best practices guide

---

## 9. Success Metrics

### Phase 1 Success Criteria
- ✅ Format detection 10x faster
- ✅ Zero P/Invoke overhead for detection
- ✅ Security validation on all paths
- ✅ Enhanced progress with ETA
- ✅ No regressions in existing tests

### Phase 2 Success Criteria
- ✅ 2x faster entry enumeration
- ✅ Constant memory usage for large files
- ✅ Cancel/pause support
- ✅ Better error context
- ✅ All platforms tested

### Phase 3 Success Criteria (if pursued)
- ✅ Measured performance gains (>20%)
- ✅ No regression in format support
- ✅ Documented use cases
- ✅ Maintainable codebase

---

## 10. Conclusion

**Recommended Approach:**
1. ✅ **Implement Phase 1** immediately (low risk, high value)
2. 🔄 **Evaluate Phase 2** after Phase 1 success
3. ⚠️ **Phase 3 only if specific needs identified**

**Key Principles:**
- Incremental, reversible changes
- Always maintain fallback to native
- Measure, don't guess
- Prioritize maintainability
- User experience first

**Next Steps:**
1. Review this plan with stakeholders
2. Create Phase 1 implementation branch
3. Implement format detection as proof of concept
4. Gather feedback and iterate

---

## 4. Managed Implementation Strategy

### 4.1 Leverage Existing .NET Ecosystem

**Don't reinvent the wheel** - use battle-tested managed libraries where available:

| Format | Managed Library | Maturity | License | Native AOT |
|--------|----------------|----------|---------|------------|
| **ZIP** | System.IO.Compression | ✅ Excellent | MIT (BCL) | ✅ Yes |
| **TAR** | Custom implementation | ⚠️ New | BSD-2 | ✅ Yes |
| **7z** | SharpCompress | ✅ Good | MIT | ⚠️ Partial |
| **Gzip** | System.IO.Compression | ✅ Excellent | MIT (BCL) | ✅ Yes |
| **Bzip2** | SharpCompress | ✅ Good | MIT | ⚠️ Partial |
| **LZ4** | K4os.Compression.LZ4 | ✅ Excellent | MIT | ✅ Yes |
| **Zstd** | ZstdSharp | ✅ Excellent | BSD-3 | ✅ Yes |
| **XZ/LZMA** | LZMA-SDK (managed) | ✅ Good | Public Domain | ✅ Yes |

### 4.2 Implementation Approach by Format

#### 4.2.1 ZIP (Highest Priority - 80% of usage)

**Managed Implementation:**
```csharp
public class ManagedZipReader : IArchiveReader
{
    private readonly ZipArchive archive;

    public ManagedZipReader(string path)
    {
        archive = ZipFile.OpenRead(path);
    }

    public IEnumerable<ArchiveEntry> Entries()
    {
        foreach (var entry in archive.Entries)
        {
            yield return new ArchiveEntry
            {
                Name = entry.FullName,
                Size = entry.Length,
                CompressedSize = entry.CompressedLength,
                ModificationTime = entry.LastWriteTime.DateTime,
                IsDirectory = entry.FullName.EndsWith("/")
            };
        }
    }

    public void ExtractToDirectory(string destinationPath)
    {
        archive.ExtractToDirectory(destinationPath);
    }
}
```

**Pros:**
- ✅ Built into .NET (no dependencies)
- ✅ Well-tested by Microsoft
- ✅ Native AOT compatible
- ✅ Cross-platform

**Cons:**
- ❌ No password/AES support in System.IO.Compression
- ❌ Limited to ZIP64 format

**Solution:** Use SharpCompress or DotNetZip for encrypted ZIP support

#### 4.2.2 TAR (Second Priority - 15% of usage)

**Managed Implementation:** Custom pure C# (TAR is simple!)

```csharp
public class ManagedTarReader : IArchiveReader
{
    private readonly Stream stream;

    public IEnumerable<TarEntry> Entries()
    {
        while (true)
        {
            var header = new byte[512];
            if (stream.Read(header, 0, 512) != 512)
                break;

            // Check for end-of-archive (two zero blocks)
            if (header.All(b => b == 0))
                break;

            var entry = ParseTarHeader(header);
            yield return entry;

            // Skip to next header (512-byte aligned)
            var skip = (entry.Size + 511) & ~511L;
            stream.Seek(skip, SeekOrigin.Current);
        }
    }

    private TarEntry ParseTarHeader(byte[] header)
    {
        return new TarEntry
        {
            Name = ReadString(header, 0, 100),
            Mode = ReadOctal(header, 100, 8),
            Size = ReadOctal(header, 124, 12),
            ModTime = DateTimeOffset.FromUnixTimeSeconds(ReadOctal(header, 136, 12)),
            Type = (TarEntryType)header[156]
        };
    }
}
```

**Complexity:** Low - TAR is a simple format (512-byte headers + data)

**Estimated effort:** 2-3 weeks for full TAR/Ustar/PAX support

#### 4.2.3 Compression Wrappers

**Use existing NuGet packages:**

```csharp
// LZ4
using K4os.Compression.LZ4;
var compressed = LZ4Pickler.Pickle(data, LZ4Level.L09_HC);

// Zstd
using ZstdSharp;
using var compressor = new Compressor();
var compressed = compressor.Wrap(data);

// Gzip/Deflate (built-in)
using System.IO.Compression;
using var gzipStream = new GzipStream(outputStream, CompressionMode.Compress);
```

**Dependencies to add:**
- K4os.Compression.LZ4 (MIT, Native AOT compatible)
- ZstdSharp (BSD-3, Native AOT compatible)
- SharpCompress (MIT, for advanced formats)

### 4.3 Runtime Selection Logic

```csharp
public static class ArchiveReaderFactory
{
    private static bool? nativeAvailable;

    public static IArchiveReader Create(string path, ArchiveFormat? format = null)
    {
        // Auto-detect format if not specified
        format ??= FormatDetector.Detect(path);

        // Try native implementation first
        if (TryCreateNative(path, format.Value, out var nativeReader))
            return nativeReader;

        // Fallback to managed
        return CreateManaged(path, format.Value);
    }

    private static bool TryCreateNative(string path, ArchiveFormat format, out IArchiveReader? reader)
    {
        // Cache result - only try loading native library once
        if (nativeAvailable == null)
        {
            try
            {
                NativeLibrary.TryLoad("libarchive", out _);
                nativeAvailable = true;
            }
            catch
            {
                nativeAvailable = false;
            }
        }

        if (nativeAvailable == true)
        {
            try
            {
                reader = new NativeArchiveReader(path);
                return true;
            }
            catch (DllNotFoundException)
            {
                nativeAvailable = false; // Update cache
            }
        }

        reader = null;
        return false;
    }

    private static IArchiveReader CreateManaged(string path, ArchiveFormat format)
    {
        return format switch
        {
            ArchiveFormat.Zip => new ManagedZipReader(path),
            ArchiveFormat.Tar => new ManagedTarReader(path),
            ArchiveFormat.Gzip => new ManagedGzipReader(path),
            ArchiveFormat.SevenZip => new ManagedSevenZipReader(path),
            _ => throw new NotSupportedException(
                $"Format {format} not yet supported in managed mode. " +
                $"Please ensure native libarchive library is available.")
        };
    }
}
```

### 4.4 Configuration Options

```csharp
public static class LibArchiveConfiguration
{
    /// <summary>
    /// Force use of managed implementation even if native is available.
    /// Useful for testing or environments where native code is restricted.
    /// </summary>
    public static bool ForceManaged { get; set; } = false;

    /// <summary>
    /// Prefer native implementation when available (default).
    /// Falls back to managed if native fails to load.
    /// </summary>
    public static bool PreferNative { get; set; } = true;

    /// <summary>
    /// Throw exception if managed fallback is used.
    /// Useful for detecting missing native libraries in production.
    /// </summary>
    public static bool RequireNative { get; set; } = false;
}
```

---

## 5. Implementation Roadmap

### Phase 1: Infrastructure + Managed ZIP (3 months)
**Deliverables:**
- [ ] IArchiveReader/IArchiveWriter interfaces
- [ ] Runtime selection facade
- [ ] NativeLibraryLoader with fallback logic
- [ ] ManagedZipReader using System.IO.Compression
- [ ] ManagedZipWriter using System.IO.Compression
- [ ] Format detection from magic bytes
- [ ] Path validation utilities
- [ ] Comprehensive tests for both paths

**Success Metric:** ZIP archives work identically on native and managed paths

### Phase 2: Managed TAR + Common Compressions (3-6 months)
**Deliverables:**
- [ ] ManagedTarReader (custom implementation)
- [ ] ManagedTarWriter (custom implementation)
- [ ] Managed Gzip/Deflate (System.IO.Compression)
- [ ] Managed Brotli (System.IO.Compression)
- [ ] Managed LZ4 (K4os.Compression.LZ4)
- [ ] Managed Zstd (ZstdSharp)
- [ ] TAR+Gzip combined reader/writer

**Success Metric:** 90% of real-world use cases work without native library

### Phase 3: Advanced Formats (6-12 months)
**Deliverables:**
- [ ] Managed 7z reader (SharpCompress or custom)
- [ ] Managed LZMA/XZ compression
- [ ] Password support for ZIP (AES encryption)
- [ ] RAR reading (if legally implementable)
- [ ] Less common formats (CPIO, ISO9660, XAR)

**Success Metric:** 100% feature parity between native and managed

### Phase 4: Optimization (Ongoing)
**Deliverables:**
- [ ] Performance profiling managed vs native
- [ ] Optimize hot paths in managed code
- [ ] Consider Span<T> and SIMD optimizations
- [ ] Memory allocation reduction
- [ ] Streaming optimizations

**Success Metric:** Managed code within 3-5x of native performance


---

## 6. Benefits of Managed Fallback Architecture

### 6.1 Deployment Flexibility

**Current:**
```
❌ Alpine Linux (musl-x64) - works (we build for musl)
❌ Alpine Linux (musl-arm64) - works (we build for it)
❌ Custom Linux distro - might fail if RID not recognized
❌ Browser/WASM - impossible (native code)
❌ iOS/Android - might fail (platform restrictions)
```

**With Managed Fallback:**
```
✅ Alpine Linux - uses managed implementation
✅ Any Linux distro - managed fallback works
✅ Browser/WASM - managed code compiles to WASM
✅ iOS - managed code works (native restricted)
✅ Android - managed code works
✅ Custom platforms - always works
```

### 6.2 Security & Sandboxing

**Managed code benefits:**
- ✅ Can run in sandboxed environments (restrictive policies)
- ✅ No unsafe code required (fully verifiable)
- ✅ Works in environments that block native DLL loading
- ✅ Better for security-conscious deployments

**Example:** Azure Functions Consumption Plan, AWS Lambda with restrictions

### 6.3 Debugging & Diagnostics

**Managed code advantages:**
- ✅ Full source stepping in debugger
- ✅ Better exception stack traces
- ✅ Memory profiling tools work better
- ✅ Easier to diagnose issues in production

### 6.4 NuGet Package Strategy

**Option A: Single Package with Managed Core**
```xml
<PackageReference Include="LibArchive.Net" Version="2.0.0" />
```
- Includes managed implementation (always works)
- Includes native libraries as optional runtime assets
- Automatically uses native if available, falls back to managed

**Option B: Separate Packages**
```xml
<!-- Managed-only (smaller, cross-platform) -->
<PackageReference Include="LibArchive.Net.Managed" Version="2.0.0" />

<!-- Native acceleration (optional) -->
<PackageReference Include="LibArchive.Net.Native" Version="2.0.0" />
```

**Recommendation:** Option A - single package, automatic selection

---

## 7. Implementation Examples

### 7.1 Basic Usage (Transparent to User)

```csharp
// User code remains EXACTLY the same
using var reader = new LibArchiveReader("archive.zip");
foreach (var entry in reader.Entries())
{
    Console.WriteLine(entry.Name);
}

// Internally:
// - Tries native libarchive first
// - Falls back to System.IO.Compression if native unavailable
// - User never knows which path was used
```

### 7.2 Explicit Mode Selection

```csharp
// Force managed mode (testing, sandboxed environments)
LibArchiveConfiguration.ForceManaged = true;
using var reader = new LibArchiveReader("archive.zip");
// Always uses managed implementation

// Require native (catch missing native libs early)
LibArchiveConfiguration.RequireNative = true;
using var reader = new LibArchiveReader("archive.zip");
// Throws if native library not available
```

### 7.3 Platform Detection & Logging

```csharp
// Diagnostic API
var info = LibArchiveInfo.GetImplementationInfo();
Console.WriteLine($"Using: {info.Implementation}"); // "Native" or "Managed"
Console.WriteLine($"Version: {info.Version}");
Console.WriteLine($"Formats: {string.Join(", ", info.SupportedFormats)}");

// Example output:
// Using: Managed
// Version: LibArchive.Net 2.0.0 (Managed Implementation)
// Formats: ZIP, TAR, Gzip, Bzip2, LZ4, Zstd
```

---

## 8. Testing Strategy

### 8.1 Dual-Path Testing

**Every test must pass on BOTH implementations:**

```csharp
[TestFixture]
public class ArchiveReaderTests
{
    [Test]
    [TestCase(Implementation.Native)]
    [TestCase(Implementation.Managed)]
    public void TestExtractZipArchive(Implementation impl)
    {
        // Configure which implementation to use
        ConfigureImplementation(impl);

        // Same test code for both paths
        using var reader = new LibArchiveReader("test.zip");
        var entries = reader.Entries().ToList();

        Assert.That(entries.Count, Is.EqualTo(5));
        // ... more assertions
    }
}
```

### 8.2 Feature Parity Matrix

Track implementation status for each format:

| Format | Native | Managed | Status |
|--------|--------|---------|--------|
| ZIP (read) | ✅ | ✅ | Complete |
| ZIP (write) | ✅ | ✅ | Complete |
| ZIP (encrypted) | ✅ | ⚠️ | Partial |
| TAR (read) | ✅ | ✅ | Complete |
| TAR (write) | ✅ | ✅ | Complete |
| 7z (read) | ✅ | ⚠️ | In Progress |
| Gzip | ✅ | ✅ | Complete |
| LZ4 | ✅ | ✅ | Complete |
| Zstd | ✅ | ✅ | Complete |

### 8.3 Performance Benchmarking

```csharp
[Benchmark]
[Arguments(Implementation.Native)]
[Arguments(Implementation.Managed)]
public void ExtractZipArchive(Implementation impl)
{
    ConfigureImplementation(impl);
    using var reader = new LibArchiveReader("large-archive.zip");
    reader.ExtractToDirectory(tempDir);
}
```

**Target:** Managed within 3-5x of native performance for common operations

---

## 9. Deployment Scenarios

### 9.1 Scenario: Azure Functions

**Challenge:** Consumption plan has restrictive sandboxing

**Current:** Might fail to load native library
**With Managed:** Always works via managed implementation

```csharp
[FunctionName("ProcessArchive")]
public static async Task<IActionResult> Run(
    [HttpTrigger] HttpRequest req,
    ILogger log)
{
    // Automatically uses managed implementation if native unavailable
    using var reader = new LibArchiveReader(req.Body);
    // ... process archive
}
```

### 9.2 Scenario: Blazor WebAssembly

**Challenge:** No native code support

**Current:** Completely impossible
**With Managed:** Full functionality via managed code compiled to WASM

```csharp
@page "/archive-viewer"
@using LibArchive.Net

<input type="file" @onchange="HandleFileSelected" />

@code {
    async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        var stream = e.File.OpenReadStream();
        // Works! Pure managed implementation compiles to WASM
        using var reader = new LibArchiveReader(stream);
        // ... display entries
    }
}
```

### 9.3 Scenario: Cross-Platform CLI Tool

**Challenge:** Support obscure platforms without building native libs

**With Managed:** Works everywhere .NET runs

```csharp
// CLI tool published as self-contained
dotnet publish -c Release -r linux-musl-arm64

// Even if we don't have native build for this RID:
// - Managed implementation works
// - User gets full functionality
// - Slower but reliable
```

---

## 10. Migration Timeline & Priorities

### Immediate (Next Release - v2.0)
- [x] Document architecture vision (this file)
- [ ] Add facade pattern infrastructure
- [ ] Implement managed ZIP reader using System.IO.Compression
- [ ] Add runtime selection logic
- [ ] Update tests to run on both paths
- [ ] Release as opt-in feature flag

### Q1 2026
- [ ] Managed ZIP writer
- [ ] Managed TAR reader/writer (custom implementation)
- [ ] Managed Gzip (System.IO.Compression)
- [ ] Make managed fallback default behavior

### Q2-Q3 2026
- [ ] Managed LZ4 (K4os.Compression.LZ4)
- [ ] Managed Zstd (ZstdSharp)
- [ ] Managed Bzip2 (SharpCompress)
- [ ] Managed 7z reader (SharpCompress)
- [ ] Performance optimization pass

### Q4 2026
- [ ] Encrypted ZIP support in managed mode
- [ ] Advanced TAR variants (PAX, GNU)
- [ ] Benchmark suite comparing native vs managed
- [ ] Production validation

### 2027+
- [ ] LZMA/XZ compression (managed)
- [ ] Less common formats (CPIO, XAR, ISO)
- [ ] SIMD optimizations where applicable
- [ ] Consider full managed implementation of all formats

---

## 11. Success Criteria

### Must Have
✅ ZIP and TAR work 100% in managed mode
✅ Automatic fallback when native unavailable  
✅ No breaking changes to public API
✅ Full test coverage for both paths
✅ Performance within 5x of native for common operations

### Nice to Have
✅ Blazor WASM support
✅ All compression algorithms in managed mode
✅ Encrypted archive support in managed mode
✅ Performance within 3x of native

### Stretch Goals
✅ All formats (30+) in pure managed code
✅ Performance within 2x of native
✅ Optional: Managed implementation faster than native for some workloads (e.g., small files with .NET 9 optimizations)

---

## 12. Risk Mitigation

### 12.1 Compatibility Testing

**Every commit must:**
- ✅ Pass all tests on native implementation
- ✅ Pass all tests on managed implementation
- ✅ Have equivalent behavior between both
- ✅ Document any known differences

### 12.2 Feature Flags

```csharp
// Gradual rollout
if (FeatureFlags.EnableManagedFallback)
{
    // New behavior
}
else
{
    // Old behavior (native-only)
}
```

### 12.3 Telemetry

```csharp
// Track which implementation is used
var telemetry = LibArchiveTelemetry.GetStats();
Console.WriteLine($"Native hits: {telemetry.NativeUsage}");
Console.WriteLine($"Managed fallbacks: {telemetry.ManagedUsage}");
Console.WriteLine($"Native load failures: {telemetry.LoadFailures}");
```

---

## 13. Summary

This architecture provides:

1. **Reliability:** Always works, even without native library
2. **Performance:** Native fast-path when available
3. **Portability:** Runs anywhere .NET runs
4. **Maintainability:** Easier to debug and extend
5. **Future-proof:** Ready for WASM, mobile, IoT, etc.

**Next Action:** Implement Phase 1 infrastructure with managed ZIP as proof of concept.
