using System.Runtime.InteropServices;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// MuslDetection tells the .NET Standard 2.0 build which of the two Linux libraries to load, glibc's
// or musl's, from the files that the process has mapped. It is internal, and the tests otherwise
// use the public API alone, so this project compiles its source in (see Test.LibArchive.Net.csproj).
//
// The excerpts are laid out as the kernel lays out /proc/<pid>/maps, for .NET 10 processes: in the
// runtime's Alpine container image on x86_64 and aarch64; on Alpine armv7 under a 32-bit kernel,
// which pads the paths to a narrower column; on Void Linux's musl edition, installed in /opt/dotnet;
// with the .NET packages of Ubuntu 24.04 and of RHEL 8, whose glibc 2.28 names its C library
// libc-2.28.so; and on Debian 13, installed by dotnet-install for a user named musl, running a
// project in a directory named ld-musl-probe. Only the paths matter: the addresses, devices and
// inodes are illustrative. Real maps end a line that has no path with a space, which is left out here.
[TestFixture]
public class MuslDetectionTests
{
    private const string AlpineX64 = """
        55b3c2a0e000-55b3c2a15000 r--p 00000000 00:3e 2373390                    /usr/share/dotnet/dotnet
        55b3c2a15000-55b3c2a22000 r-xp 00007000 00:3e 2373390                    /usr/share/dotnet/dotnet
        55b3c2a22000-55b3c2a25000 r--p 00014000 00:3e 2373390                    /usr/share/dotnet/dotnet
        55b3c2a25000-55b3c2a26000 rw-p 00016000 00:3e 2373390                    /usr/share/dotnet/dotnet
        55b3c3f47000-55b3c3f68000 rw-p 00000000 00:00 0                          [heap]
        7f15e4000000-7f15e4021000 rw-p 00000000 00:00 0
        7f15ea6f9000-7f15ea6fa000 r-xs 00000000 00:01 3074                       /memfd:doublemapper (deleted)
        7f15eb1c0000-7f15eb6c4000 r--p 00000000 00:3e 2373542                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        7f15eb7f0000-7f15eb7f2000 r--p 00000000 00:3e 2369121                    /app/App.dll
        7f15eb81d000-7f15eb872000 r--p 00000000 00:3e 2373461                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        7f15eb872000-7f15ebaa4000 r-xp 00055000 00:3e 2373461                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        7f15ec5a0000-7f15ec660000 r--p 00000000 00:3e 2373463                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f15ec660000-7f15ec9a1000 r-xp 000c0000 00:3e 2373463                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f15ecb10000-7f15ecb1c000 r--p 00000000 00:3e 2373470                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libSystem.Native.so
        7f15ecc31000-7f15ecc3e000 r--p 00000000 00:3e 2373466                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libhostpolicy.so
        7f15ecc7d000-7f15ecc8b000 r--p 00000000 00:3e 2373378                    /usr/share/dotnet/host/fxr/10.0.0/libhostfxr.so
        7f15ecd02000-7f15ecd16000 r--p 00000000 00:3e 1053703                    /usr/lib/libstdc++.so.6.0.33
        7f15ecd16000-7f15ecde8000 r-xp 00014000 00:3e 1053703                    /usr/lib/libstdc++.so.6.0.33
        7f15ece5a000-7f15ece5d000 r--p 00000000 00:3e 1053688                    /usr/lib/libgcc_s.so.1
        7f15ece5d000-7f15ece75000 r-xp 00003000 00:3e 1053688                    /usr/lib/libgcc_s.so.1
        7f15ece7b000-7f15ece8f000 r--p 00000000 00:3e 1052967                    /lib/ld-musl-x86_64.so.1
        7f15ece8f000-7f15ecedb000 r-xp 00014000 00:3e 1052967                    /lib/ld-musl-x86_64.so.1
        7f15ecedb000-7f15ecf11000 r--p 00060000 00:3e 1052967                    /lib/ld-musl-x86_64.so.1
        7f15ecf11000-7f15ecf12000 r--p 00095000 00:3e 1052967                    /lib/ld-musl-x86_64.so.1
        7f15ecf12000-7f15ecf13000 rw-p 00096000 00:3e 1052967                    /lib/ld-musl-x86_64.so.1
        7f15ecf13000-7f15ecf16000 rw-p 00000000 00:00 0
        7ffc8d1b2000-7ffc8d1d3000 rw-p 00000000 00:00 0                          [stack]
        7ffc8d1f5000-7ffc8d1f9000 r--p 00000000 00:00 0                          [vvar]
        7ffc8d1f9000-7ffc8d1fb000 r-xp 00000000 00:00 0                          [vdso]
        ffffffffff600000-ffffffffff601000 --xp 00000000 00:00 0                  [vsyscall]
        """;

    private const string AlpineArm64 = """
        aaaad8e70000-aaaad8e7a000 r-xp 00000000 00:3e 2373390                    /usr/share/dotnet/dotnet
        aaaad8e89000-aaaad8e8a000 r--p 00009000 00:3e 2373390                    /usr/share/dotnet/dotnet
        aaaad8e8a000-aaaad8e8b000 rw-p 0000a000 00:3e 2373390                    /usr/share/dotnet/dotnet
        aaab0b7c2000-aaab0b7e3000 rw-p 00000000 00:00 0                          [heap]
        ffff6c000000-ffff6c021000 rw-p 00000000 00:00 0
        ffff7a3c0000-ffff7a3c1000 r-xs 00000000 00:01 2061                       /memfd:doublemapper (deleted)
        ffff7b0a0000-ffff7b5f4000 r--p 00000000 00:3e 2373542                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        ffff7b8c0000-ffff7bb52000 r-xp 00000000 00:3e 2373461                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        ffff7bb61000-ffff7bb6e000 r--p 00291000 00:3e 2373461                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        ffff7bb6e000-ffff7bb70000 rw-p 0029e000 00:3e 2373461                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        ffff7bc10000-ffff7c0ee000 r-xp 00000000 00:3e 2373463                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        ffff7c0fd000-ffff7c13a000 r--p 004dd000 00:3e 2373463                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        ffff7c13a000-ffff7c143000 rw-p 0051a000 00:3e 2373463                    /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        ffff7c2d0000-ffff7c42e000 r-xp 00000000 00:3e 1053703                    /usr/lib/libstdc++.so.6.0.33
        ffff7c43d000-ffff7c44a000 r--p 0015d000 00:3e 1053703                    /usr/lib/libstdc++.so.6.0.33
        ffff7c44a000-ffff7c44d000 rw-p 0016a000 00:3e 1053703                    /usr/lib/libstdc++.so.6.0.33
        ffff7c460000-ffff7c47f000 r-xp 00000000 00:3e 1053688                    /usr/lib/libgcc_s.so.1
        ffff7c48e000-ffff7c48f000 r--p 0001e000 00:3e 1053688                    /usr/lib/libgcc_s.so.1
        ffff7c48f000-ffff7c490000 rw-p 0001f000 00:3e 1053688                    /usr/lib/libgcc_s.so.1
        ffff7c4a0000-ffff7c54a000 r-xp 00000000 00:3e 1052967                    /lib/ld-musl-aarch64.so.1
        ffff7c558000-ffff7c559000 r--p 000a8000 00:3e 1052967                    /lib/ld-musl-aarch64.so.1
        ffff7c559000-ffff7c55a000 rw-p 000a9000 00:3e 1052967                    /lib/ld-musl-aarch64.so.1
        ffff7c55a000-ffff7c55d000 rw-p 00000000 00:00 0
        ffff7c55d000-ffff7c55f000 r--p 00000000 00:00 0                          [vvar]
        ffff7c55f000-ffff7c560000 r-xp 00000000 00:00 0                          [vdso]
        ffffd6a41000-ffffd6a62000 rw-p 00000000 00:00 0                          [stack]
        """;

    private const string AlpineArmv7 = """
        7f5c1000-7f5cb000 r-xp 00000000 b3:02 263175     /usr/share/dotnet/dotnet
        7f5da000-7f5db000 r--p 00009000 b3:02 263175     /usr/share/dotnet/dotnet
        7f5db000-7f5dc000 rw-p 0000a000 b3:02 263175     /usr/share/dotnet/dotnet
        80a5e000-80a7f000 rw-p 00000000 00:00 0          [heap]
        b1d00000-b1d21000 rw-p 00000000 00:00 0
        b3a6e000-b3a6f000 r-xs 00000000 00:05 4127       /memfd:doublemapper (deleted)
        b4b80000-b50b8000 r--p 00000000 b3:02 263311     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        b5c40000-b5e1e000 r-xp 00000000 b3:02 263287     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        b5e2d000-b5e35000 r--p 001dd000 b3:02 263287     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        b5e35000-b5e36000 rw-p 001e5000 b3:02 263287     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libclrjit.so
        b5f00000-b6290000 r-xp 00000000 b3:02 263289     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        b629f000-b62c6000 r--p 0038f000 b3:02 263289     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        b62c6000-b62cc000 rw-p 003b6000 b3:02 263289     /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        b6c3c000-b6d28000 r-xp 00000000 b3:02 131561     /usr/lib/libstdc++.so.6.0.33
        b6d37000-b6d3d000 r--p 000eb000 b3:02 131561     /usr/lib/libstdc++.so.6.0.33
        b6d3d000-b6d3e000 rw-p 000f1000 b3:02 131561     /usr/lib/libstdc++.so.6.0.33
        b6d50000-b6d6c000 r-xp 00000000 b3:02 131548     /usr/lib/libgcc_s.so.1
        b6d7b000-b6d7c000 r--p 0001b000 b3:02 131548     /usr/lib/libgcc_s.so.1
        b6d7c000-b6d7d000 rw-p 0001c000 b3:02 131548     /usr/lib/libgcc_s.so.1
        b6e5c000-b6ef1000 r-xp 00000000 b3:02 131145     /lib/ld-musl-armhf.so.1
        b6f00000-b6f01000 r--p 00094000 b3:02 131145     /lib/ld-musl-armhf.so.1
        b6f01000-b6f02000 rw-p 00095000 b3:02 131145     /lib/ld-musl-armhf.so.1
        b6f02000-b6f04000 rw-p 00000000 00:00 0
        bec4e000-bec6f000 rw-p 00000000 00:00 0          [stack]
        bef1e000-bef1f000 r-xp 00000000 00:00 0          [sigpage]
        ffff0000-ffff1000 r-xp 00000000 00:00 0          [vectors]
        """;

    private const string VoidX64 = """
        5604c9a51000-5604c9a58000 r--p 00000000 08:02 1844563                    /opt/dotnet/dotnet
        5604c9a58000-5604c9a65000 r-xp 00007000 08:02 1844563                    /opt/dotnet/dotnet
        5604c9a65000-5604c9a68000 r--p 00014000 08:02 1844563                    /opt/dotnet/dotnet
        5604c9a68000-5604c9a69000 rw-p 00016000 08:02 1844563                    /opt/dotnet/dotnet
        5604cb2e4000-5604cb305000 rw-p 00000000 00:00 0                          [heap]
        7fa1d8000000-7fa1d8021000 rw-p 00000000 00:00 0
        7fa1de3ff000-7fa1de400000 r-xs 00000000 00:01 2058                       /memfd:doublemapper (deleted)
        7fa1df0c0000-7fa1df5c4000 r--p 00000000 08:02 1844790                    /opt/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        7fa1df7a0000-7fa1df860000 r--p 00000000 08:02 1844712                    /opt/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7fa1df860000-7fa1dfba1000 r-xp 000c0000 08:02 1844712                    /opt/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7fa1dfd02000-7fa1dfd16000 r--p 00000000 08:02 1311780                    /usr/lib/libstdc++.so.6.0.33
        7fa1dfd16000-7fa1dfde8000 r-xp 00014000 08:02 1311780                    /usr/lib/libstdc++.so.6.0.33
        7fa1dfe5a000-7fa1dfe5d000 r--p 00000000 08:02 1311764                    /usr/lib/libgcc_s.so.1
        7fa1dfe5d000-7fa1dfe75000 r-xp 00003000 08:02 1311764                    /usr/lib/libgcc_s.so.1
        7fa1dfe7b000-7fa1dfe8f000 r--p 00000000 08:02 1310971                    /usr/lib/libc.so
        7fa1dfe8f000-7fa1dfedb000 r-xp 00014000 08:02 1310971                    /usr/lib/libc.so
        7fa1dfedb000-7fa1dff11000 r--p 00060000 08:02 1310971                    /usr/lib/libc.so
        7fa1dff11000-7fa1dff12000 r--p 00095000 08:02 1310971                    /usr/lib/libc.so
        7fa1dff12000-7fa1dff13000 rw-p 00096000 08:02 1310971                    /usr/lib/libc.so
        7fa1dff13000-7fa1dff16000 rw-p 00000000 00:00 0
        7ffe5c0f1000-7ffe5c112000 rw-p 00000000 00:00 0                          [stack]
        7ffe5c1b5000-7ffe5c1b9000 r--p 00000000 00:00 0                          [vvar]
        7ffe5c1b9000-7ffe5c1bb000 r-xp 00000000 00:00 0                          [vdso]
        ffffffffff600000-ffffffffff601000 --xp 00000000 00:00 0                  [vsyscall]
        """;

    private const string UbuntuX64 = """
        5612e1f3e000-5612e1f45000 r--p 00000000 08:01 3933247                    /usr/lib/dotnet/dotnet
        5612e1f45000-5612e1f52000 r-xp 00007000 08:01 3933247                    /usr/lib/dotnet/dotnet
        5612e1f52000-5612e1f55000 r--p 00014000 08:01 3933247                    /usr/lib/dotnet/dotnet
        5612e1f55000-5612e1f56000 rw-p 00016000 08:01 3933247                    /usr/lib/dotnet/dotnet
        5612e3b1f000-5612e3b40000 rw-p 00000000 00:00 0                          [heap]
        7f2a68000000-7f2a68021000 rw-p 00000000 00:00 0
        7f2a6e5ff000-7f2a6e600000 r-xs 00000000 00:01 2059                       /memfd:doublemapper (deleted)
        7f2a6f040000-7f2a6f544000 r--p 00000000 08:01 3933790                    /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        7f2a6f9a0000-7f2a6fa60000 r--p 00000000 08:01 3933712                    /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f2a6fa60000-7f2a6fda1000 r-xp 000c0000 08:01 3933712                    /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f2a70000000-7f2a701a5000 r--p 00000000 08:01 3803412                    /usr/lib/x86_64-linux-gnu/libicudata.so.74.2
        7f2a70400000-7f2a704a2000 r--p 00000000 08:01 3803419                    /usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.33
        7f2a704a2000-7f2a705d3000 r-xp 000a2000 08:01 3803419                    /usr/lib/x86_64-linux-gnu/libstdc++.so.6.0.33
        7f2a70600000-7f2a70628000 r--p 00000000 08:01 3803138                    /usr/lib/x86_64-linux-gnu/libc.so.6
        7f2a70628000-7f2a707b0000 r-xp 00028000 08:01 3803138                    /usr/lib/x86_64-linux-gnu/libc.so.6
        7f2a707b0000-7f2a707ff000 r--p 001b0000 08:01 3803138                    /usr/lib/x86_64-linux-gnu/libc.so.6
        7f2a707ff000-7f2a70803000 r--p 001fe000 08:01 3803138                    /usr/lib/x86_64-linux-gnu/libc.so.6
        7f2a70803000-7f2a70805000 rw-p 00202000 08:01 3803138                    /usr/lib/x86_64-linux-gnu/libc.so.6
        7f2a70805000-7f2a70812000 rw-p 00000000 00:00 0
        7f2a70a5d000-7f2a70a61000 r--p 00000000 08:01 3803201                    /usr/lib/x86_64-linux-gnu/libgcc_s.so.1
        7f2a70a61000-7f2a70a84000 r-xp 00004000 08:01 3803201                    /usr/lib/x86_64-linux-gnu/libgcc_s.so.1
        7f2a70b4c000-7f2a70b4d000 r--p 00000000 08:01 3803112                    /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f2a70b4d000-7f2a70b78000 r-xp 00001000 08:01 3803112                    /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f2a70b78000-7f2a70b82000 r--p 0002c000 08:01 3803112                    /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f2a70b82000-7f2a70b84000 r--p 00036000 08:01 3803112                    /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f2a70b84000-7f2a70b86000 rw-p 00038000 08:01 3803112                    /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7ffd7b2f1000-7ffd7b312000 rw-p 00000000 00:00 0                          [stack]
        7ffd7b3b5000-7ffd7b3b9000 r--p 00000000 00:00 0                          [vvar]
        7ffd7b3b9000-7ffd7b3bb000 r-xp 00000000 00:00 0                          [vdso]
        ffffffffff600000-ffffffffff601000 --xp 00000000 00:00 0                  [vsyscall]
        """;

    private const string Rhel8X64 = """
        55c86f2a3000-55c86f2aa000 r--p 00000000 fd:00 101453877                  /usr/lib64/dotnet/dotnet
        55c86f2aa000-55c86f2b7000 r-xp 00007000 fd:00 101453877                  /usr/lib64/dotnet/dotnet
        55c86f2b7000-55c86f2ba000 r--p 00014000 fd:00 101453877                  /usr/lib64/dotnet/dotnet
        55c86f2ba000-55c86f2bb000 rw-p 00016000 fd:00 101453877                  /usr/lib64/dotnet/dotnet
        55c870c4e000-55c870c6f000 rw-p 00000000 00:00 0                          [heap]
        7f3e20000000-7f3e20021000 rw-p 00000000 00:00 0
        7f3e263ff000-7f3e26400000 r-xs 00000000 00:01 2056                       /memfd:doublemapper (deleted)
        7f3e27040000-7f3e27544000 r--p 00000000 fd:00 101454102                  /usr/lib64/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        7f3e279a0000-7f3e27a60000 r--p 00000000 fd:00 101454031                  /usr/lib64/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f3e27a60000-7f3e27da1000 r-xp 000c0000 fd:00 101454031                  /usr/lib64/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f3e2c2c1000-7f3e2c3ad000 r-xp 00000000 fd:00 33600291                   /usr/lib64/libstdc++.so.6.0.25
        7f3e2c3ad000-7f3e2c5ad000 ---p 000ec000 fd:00 33600291                   /usr/lib64/libstdc++.so.6.0.25
        7f3e2c5ad000-7f3e2c5b7000 r--p 000ec000 fd:00 33600291                   /usr/lib64/libstdc++.so.6.0.25
        7f3e2c5b7000-7f3e2c5b9000 rw-p 000f6000 fd:00 33600291                   /usr/lib64/libstdc++.so.6.0.25
        7f3e2c5d1000-7f3e2c5e8000 r-xp 00000000 fd:00 33600128                   /usr/lib64/libgcc_s-8-20210514.so.1
        7f3e2c5e8000-7f3e2c7e7000 ---p 00017000 fd:00 33600128                   /usr/lib64/libgcc_s-8-20210514.so.1
        7f3e2c7e7000-7f3e2c7e8000 r--p 00016000 fd:00 33600128                   /usr/lib64/libgcc_s-8-20210514.so.1
        7f3e2c7e8000-7f3e2c7e9000 rw-p 00017000 fd:00 33600128                   /usr/lib64/libgcc_s-8-20210514.so.1
        7f3e2c9b1000-7f3e2cb6a000 r-xp 00000000 fd:00 33600133                   /usr/lib64/libc-2.28.so
        7f3e2cb6a000-7f3e2cd69000 ---p 001b9000 fd:00 33600133                   /usr/lib64/libc-2.28.so
        7f3e2cd69000-7f3e2cd6d000 r--p 001b8000 fd:00 33600133                   /usr/lib64/libc-2.28.so
        7f3e2cd6d000-7f3e2cd6f000 rw-p 001bc000 fd:00 33600133                   /usr/lib64/libc-2.28.so
        7f3e2cd6f000-7f3e2cd73000 rw-p 00000000 00:00 0
        7f3e2cd73000-7f3e2cef4000 r-xp 00000000 fd:00 33600137                   /usr/lib64/libm-2.28.so
        7f3e2d0f6000-7f3e2d0fd000 r-xp 00000000 fd:00 33600149                   /usr/lib64/librt-2.28.so
        7f3e2d2fe000-7f3e2d301000 r-xp 00000000 fd:00 33600135                   /usr/lib64/libdl-2.28.so
        7f3e2d502000-7f3e2d51d000 r-xp 00000000 fd:00 33600145                   /usr/lib64/libpthread-2.28.so
        7f3e2d73a000-7f3e2d767000 r-xp 00000000 fd:00 33600126                   /usr/lib64/ld-2.28.so
        7f3e2d967000-7f3e2d968000 r--p 0002d000 fd:00 33600126                   /usr/lib64/ld-2.28.so
        7f3e2d968000-7f3e2d969000 rw-p 0002e000 fd:00 33600126                   /usr/lib64/ld-2.28.so
        7ffc61e2a000-7ffc61e4b000 rw-p 00000000 00:00 0                          [stack]
        7ffc61f5b000-7ffc61f5f000 r--p 00000000 00:00 0                          [vvar]
        7ffc61f5f000-7ffc61f61000 r-xp 00000000 00:00 0                          [vdso]
        ffffffffff600000-ffffffffff601000 --xp 00000000 00:00 0                  [vsyscall]
        """;

    private const string DebianX64WithMuslInPaths = """
        55e0f7a8c000-55e0f7a93000 r--p 00000000 fe:01 1311025                    /home/musl/.dotnet/dotnet
        55e0f7a93000-55e0f7aa0000 r-xp 00007000 fe:01 1311025                    /home/musl/.dotnet/dotnet
        55e0f7aa0000-55e0f7aa3000 r--p 00014000 fe:01 1311025                    /home/musl/.dotnet/dotnet
        55e0f7aa3000-55e0f7aa4000 rw-p 00016000 fe:01 1311025                    /home/musl/.dotnet/dotnet
        55e0f8d3a000-55e0f8d5b000 rw-p 00000000 00:00 0                          [heap]
        7f9d34000000-7f9d34021000 rw-p 00000000 00:00 0
        7f9d3a1ff000-7f9d3a200000 r-xs 00000000 00:01 2051                       /memfd:doublemapper (deleted)
        7f9d3c3f0000-7f9d3c3f6000 r--p 00000000 fe:01 1323870                    /home/musl/Projects/ld-musl-probe/bin/Debug/net10.0/MuslProbe.dll
        7f9d3c400000-7f9d3c904000 r--p 00000000 fe:01 1311470                    /home/musl/.dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll
        7f9d3d4a0000-7f9d3d560000 r--p 00000000 fe:01 1311392                    /home/musl/.dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f9d3d560000-7f9d3d8a1000 r-xp 000c0000 fe:01 1311392                    /home/musl/.dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
        7f9d3d9b0000-7f9d3d9bd000 r--p 00000000 fe:01 1311386                    /home/musl/.dotnet/shared/Microsoft.NETCore.App/10.0.0/libhostpolicy.so
        7f9d3d9f0000-7f9d3d9fe000 r--p 00000000 fe:01 1311090                    /home/musl/.dotnet/host/fxr/10.0.0/libhostfxr.so
        7f9d3dc00000-7f9d3dc28000 r--p 00000000 fe:01 524397                     /usr/lib/x86_64-linux-gnu/libc.so.6
        7f9d3dc28000-7f9d3dd93000 r-xp 00028000 fe:01 524397                     /usr/lib/x86_64-linux-gnu/libc.so.6
        7f9d3dd93000-7f9d3dde9000 r--p 00193000 fe:01 524397                     /usr/lib/x86_64-linux-gnu/libc.so.6
        7f9d3dde9000-7f9d3dded000 r--p 001e8000 fe:01 524397                     /usr/lib/x86_64-linux-gnu/libc.so.6
        7f9d3dded000-7f9d3ddef000 rw-p 001ec000 fe:01 524397                     /usr/lib/x86_64-linux-gnu/libc.so.6
        7f9d3dee5000-7f9d3dee6000 r--p 00000000 fe:01 524390                     /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f9d3dee6000-7f9d3df0b000 r-xp 00001000 fe:01 524390                     /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f9d3df0b000-7f9d3df16000 r--p 00026000 fe:01 524390                     /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f9d3df16000-7f9d3df18000 r--p 00031000 fe:01 524390                     /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7f9d3df18000-7f9d3df1a000 rw-p 00033000 fe:01 524390                     /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2
        7ffe2c6b9000-7ffe2c6da000 rw-p 00000000 00:00 0                          [stack]
        7ffe2c7c1000-7ffe2c7c5000 r--p 00000000 00:00 0                          [vvar]
        7ffe2c7c5000-7ffe2c7c7000 r-xp 00000000 00:00 0                          [vdso]
        """;

    [Test]
    public void AlpineX64IsMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(AlpineX64), Is.True);
    }

    [Test]
    public void AlpineArm64IsMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(AlpineArm64), Is.True);
    }

    [Test]
    public void AlpineArmv7IsMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(AlpineArmv7), Is.True);
    }

    // On Void Linux, as after an upstream make install of musl, /lib/ld-musl-x86_64.so.1 is a
    // symbolic link to libc.so, so the maps show /usr/lib/libc.so
    [Test]
    public void VoidX64IsMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(VoidX64), Is.True);
    }

    // A process that outlives an upgrade of musl maps a file since deleted, whose path the kernel
    // marks by appending " (deleted)"
    [Test]
    public void DeletedLibcSoIsMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(VoidX64.Replace("/usr/lib/libc.so", "/usr/lib/libc.so (deleted)")), Is.True);
    }

    [Test]
    public void GlibcIsNotMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(UbuntuX64), Is.False);
    }

    // Only a file named exactly libc.so counts: glibc's C library is libc.so.6 (UbuntuX64), or, before
    // glibc 2.34, libc-2.<n>.so, to which libc.so.6 is a symbolic link
    [Test]
    public void Glibc228IsNotMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(Rhel8X64), Is.False);
    }

    // Only the name of a mapped file counts, and only when it starts with ld-musl-: not a directory
    // such as /home/musl or ld-musl-probe, nor a file name such as MuslProbe.dll
    [Test]
    public void GlibcWithMuslInPathsIsNotMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(DebianX64WithMuslInPaths), Is.False);
    }

    [Test]
    public void EmptyMapsAreNotMusl()
    {
        Assert.That(MuslDetection.IsMuslProcess(""), Is.False);
    }

    // The maps of this process agree with its C library: glibc exports gnu_get_libc_version, and
    // musl does not
    [Test]
    public void LiveMapsAgreeWithTheCLibrary()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.Ignore("Only Linux has /proc/self/maps and a glibc or musl C library");

        var musl = !ResolvesGnuGetLibcVersion();

        // Read here as well, so that maps which cannot be read fail the test rather than pass it as glibc's
        Assert.That(MuslDetection.IsMuslProcess(File.ReadAllText("/proc/self/maps")), Is.EqualTo(musl));
        Assert.That(MuslDetection.IsMuslProcess(), Is.EqualTo(musl));
    }

    // Whether gnu_get_libc_version resolves. On musl the name libc.so.6 loads musl, whose dynamic
    // linker takes any library named libc.* to be itself, and the entry point is not found there.
    private static bool ResolvesGnuGetLibcVersion()
    {
        try
        {
            gnu_get_libc_version();
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc.so.6")]
    private static extern IntPtr gnu_get_libc_version();
}
