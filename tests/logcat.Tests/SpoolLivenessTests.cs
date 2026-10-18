using System;
using System.IO;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// 暂存目录活性锁原语（任务 1.1–1.3，决策 D1/D2/D3）：
/// 持有期间探测判有主、释放后判无主、无锁文件判无主、
/// 本进程自持有的锁探测同样判有主（Windows 按句柄校验、不按进程——单一判据取代双轨制的关键依据）、
/// 建目录-建锁微秒窗口的重试收窄与二次失败上报（不抛未处理异常、无半成品残留）。
/// 全部走 internal 指定根目录重载（单测用临时根，不碰应用目录）。
/// </summary>
public class SpoolLivenessTests
{
    [Fact]
    public void 持锁期间探测有主_释放后探测无主()
    {
        var root = NewRoot();
        try
        {
            var lease = SpoolPaths.NewSessionDir(root);

            Assert.True(Directory.Exists(lease.Dir));
            Assert.True(SpoolPaths.IsDirAlive(lease.Dir));    // 本进程持有的锁：探测同样判有主（按句柄不按进程）
            Assert.True(File.Exists(Path.Combine(lease.Dir, SpoolPaths.LockFileName)));

            lease.Release();
            Assert.False(SpoolPaths.IsDirAlive(lease.Dir));   // 释放后锁文件空闲 → 无主
            lease.Release();                                  // 幂等：重复释放不抛
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 无锁文件目录判无主()
    {
        var root = NewRoot();
        try
        {
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "part-0.log"), "x");   // 存量 / 崩溃残留：无锁文件

            Assert.False(SpoolPaths.IsDirAlive(dir));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 探测异常面_不存在_被占_空闲三种输入均有确定结论()
    {
        var root = NewRoot();
        try
        {
            // ① 目录本身不存在 → 无主，不抛
            Assert.False(SpoolPaths.IsDirAlive(Path.Combine(root, "not-exist")));

            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            // ② 锁文件不存在 → 无主
            Assert.False(SpoolPaths.IsDirAlive(dir));

            var lockPath = Path.Combine(dir, SpoolPaths.LockFileName);
            // ③ 存在但被占（与持有者同口径的 FileShare.Delete 句柄）→ 有主
            using (var held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete))
                Assert.True(SpoolPaths.IsDirAlive(dir));
            // ④ 存在且空闲 → 无主（且探测句柄自身不留下占用）
            Assert.False(SpoolPaths.IsDirAlive(dir));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 建目录建锁竞态_目录被删后重建重试一次成功()
    {
        var root = NewRoot();
        try
        {
            int calls = 0;
            var lease = SpoolPaths.NewSessionDir(root, dir =>
            {
                if (calls++ == 0) Directory.Delete(dir, recursive: true);   // 模拟 D3 微秒窗口：建锁前目录被清理删掉
            });

            Assert.Equal(2, calls);                        // 恰好重试一次
            Assert.True(Directory.Exists(lease.Dir));
            Assert.True(SpoolPaths.IsDirAlive(lease.Dir)); // 重试后句柄可持有
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void 建目录建锁竞态_重试仍失败_抛IO错不抛未处理异常()
    {
        var root = NewRoot();
        try
        {
            // 两次建锁前目录都被删掉 → 二次失败上报 I/O 错（由打开路径提示用户重试，非数据损坏）
            Assert.ThrowsAny<IOException>(
                () => SpoolPaths.NewSessionDir(root, dir => Directory.Delete(dir, recursive: true)));

            // 失败路径不留半成品锁文件
            foreach (var dir in Directory.EnumerateDirectories(root))
                Assert.False(File.Exists(Path.Combine(dir, SpoolPaths.LockFileName)));
        }
        finally { Cleanup(root); }
    }

    static string NewRoot() => Path.Combine(TempRoot.Dir, "liveness-" + Guid.NewGuid().ToString("N"));

    static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
