using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

public class SevenZipTests
{
    private readonly SHA256 hash = SHA256.Create();

    [Test]
    public void Test7z()
    {
        using var lar = new LibArchiveReader("7ztest.7z");
        foreach (var e in lar.Entries())
        {
            using var s = e.Stream;
            StringBuilder sb = new(e.Name,e.Name.Length+33);
            sb.Append(' ');
            foreach (var d in hash.ComputeHash(s))
            {
                sb.Append(d.ToString("x2"));
            }
            Console.WriteLine(sb);
        }
        Assert.Pass();
    }

    [Test]
    public void TestMultiRar()
    {
        var files = Directory.GetFiles(TestContext.CurrentContext.TestDirectory, "rartest*.rar");
        Array.Sort(files);
        Assert.That(files, Has.Length.EqualTo(4), "Expected 4 RAR segments");
        using var rar = new LibArchiveReader(files);
        foreach (var e in rar.Entries())
        {
            using var s = e.Stream;
            StringBuilder sb = new(e.Name, e.Name.Length + 33);
            sb.Append(' ');
            foreach (var d in hash.ComputeHash(s))
            {
                sb.Append(d.ToString("x2"));
            }
            Console.WriteLine(sb);
        }
    }
}