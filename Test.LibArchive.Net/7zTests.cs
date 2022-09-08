using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;

namespace Test.LibArchive.Net;

public class SevenZipTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void Test1()
    {
        var hash=SHA256.Create();
        using var lar = new LibArchiveReader("7ztest.7z");
        foreach (var e in lar.Entries())
        {
            Console.WriteLine(e.Name);
            using var s = e.Stream;
            StringBuilder sb = new();
            foreach (var d in hash.ComputeHash(s))
            {
                sb.Append(d.ToString("x2"));
            }
            Console.WriteLine(sb);
        }
        Assert.Pass();
    }
}