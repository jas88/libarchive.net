using System.Runtime.InteropServices;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

[TestFixture]
public class DisposableStringArrayTests
{
    // The bytes of the NUL-terminated string at p
    private static byte[] ReadNarrowString(IntPtr p)
    {
        var bytes = new List<byte>();
        for (var i = 0; Marshal.ReadByte(p, i) != 0; i++)
            bytes.Add(Marshal.ReadByte(p, i));
        return bytes.ToArray();
    }

    [Test]
    public void PointsToANullTerminatedArrayOfUtf8Strings()
    {
        string[] strings = { "a.tar", "Größe-日本.tar" };
        using var array = new DisposableStringArray(strings);

        for (var i = 0; i < strings.Length; i++)
            Assert.That(ReadNarrowString(Marshal.ReadIntPtr(array.Ptr, i * IntPtr.Size)), Is.EqualTo(Encoding.UTF8.GetBytes(strings[i])));
        Assert.That(Marshal.ReadIntPtr(array.Ptr, strings.Length * IntPtr.Size), Is.EqualTo(IntPtr.Zero));
    }

    // A null string fails the constructor with ArgumentNullException, after it has freed the
    // strings marshalled before it (the slots after it, never filled, are skipped)
    [Test]
    public void NullStringThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DisposableStringArray(new[] { "a", null!, "c" }).Dispose());
    }
}
