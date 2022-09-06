using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

public class LibArchiveReader : SafeHandleZeroOrMinusOneIsInvalid
{
    public LibArchiveReader(string filename) : base(true)
    {
        using var uName = new SafeStringBuffer(filename);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);
        if (archive_read_open_filename(handle, uName.Ptr, 16384) != 0)
            throw new ApplicationException("TODO: Archive open failed");
    }

    protected override bool ReleaseHandle()
    {
        return archive_read_free(handle) == 0;
    }
    
    public class FileStream : Stream
    {
        private readonly IntPtr _archive;
        protected FileStream(IntPtr archive)
        {
            this._archive = archive;
        }
        
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return archive_read_data(_archive, ref MemoryMarshal.GetReference(buffer.AsSpan()[offset..]), count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    [DllImport("archive")]
    private static extern IntPtr archive_read_new();

    [DllImport("archive")]
    private static extern void archive_read_support_filter_all(IntPtr a);
    
    [DllImport("archive")]
    private static extern void archive_read_support_format_all(IntPtr a);

    [DllImport("archive")]
    private static extern int archive_read_open_filename(IntPtr a, IntPtr filename, int blocksize);

    [DllImport("archive")]
    private static extern int archive_read_data(IntPtr a, ref byte buff, int size);

    [DllImport("archive")]
    private static extern int archive_read_free(IntPtr a);
}