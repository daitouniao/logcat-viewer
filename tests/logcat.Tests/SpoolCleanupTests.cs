using System;
using System.Collections.Generic;
using System.IO;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// spool 文件名规则（任务 4.4）与启动后台清理（任务 6.2）：
/// 取原文件名、空名回退 proxy.log、&gt;64 字符截断为头 48 + 尾 16（尾段含扩展名）；
/// 启动清理残留全删、活跃 guid 目录跳过、逐目录删不掉只留痕不抛（真值口径见 spec）。
/// 全部走 internal 指定根目录重载（单测用临时根，不碰应用目录）。
/// </summary>
public class SpoolCleanupTests
{
    // ── 4.4 文件名规则 ──

    [Fact]
    public void SpoolFileNameFor_取原文件名()
    {
        Assert.Equal("a.log", SpoolPaths.SpoolFileNameFor(@"C:\logs\a.log"));
        Assert.Equal("crash.txt", SpoolPaths.SpoolFileNameFor("crash.txt"));
    }

    [Fact]
    public void SpoolFileNameFor_空名回退proxylog()
    {
        Assert.Equal("proxy.log", SpoolPaths.SpoolFileNameFor(""));
        Assert.Equal("proxy.log", SpoolPaths.SpoolFileNameFor(null));
        Assert.Equal("proxy.log", SpoolPaths.SpoolFileNameFor(@"C:\dir\"));   // 无文件名成分
    }

    [Fact]
    public void SpoolFileNameFor_超长名截断头48尾16_拼接路径仍合法()
    {
        // 75 字符原文件名 → 头 48 + 尾 16 = 64 字符，尾段保留扩展名
        string longName = new string('A', 40) + "." + new string('B', 30) + ".log";   // 40+1+30+4 = 75
        Assert.True(longName.Length > 64);
        var name = SpoolPaths.SpoolFileNameFor(@"C:\logs\" + longName);

        Assert.Equal(64, name.Length);
        Assert.Equal(".log", System.IO.Path.GetExtension(name));
        Assert.Equal(longName[..48] + longName[^16..], name);   // 头 48 + 尾 16 口径

        // 拼接后的 spool 路径可真实落盘（合法性用文件系统验证，不只是字符串断言）
        var dir = Path.Combine(TempRoot.Dir, "spoolname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] bytes = { 1, 2, 3 };
            var spoolPath = Path.Combine(dir, name);
            File.WriteAllBytes(spoolPath, bytes);
            Assert.Equal(bytes, File.ReadAllBytes(spoolPath));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SpoolFileNameFor_64字符边界不截断()
    {
        string name64 = new string('x', 64);
        Assert.Equal(name64, SpoolPaths.SpoolFileNameFor(@"C:\" + name64));
    }

    // ── 6.2 启动后台清理 ──

    [Fact]
    public void 启动清理_残留全删_活跃目录跳过()
    {
        var root = Path.Combine(TempRoot.Dir, "cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dead1 = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var dead2 = Path.Combine(root, Guid.NewGuid().ToString("N"));
            foreach (var d in new[] { dead1, dead2 })
            {
                Directory.CreateDirectory(d);   // 崩溃残留：无锁文件
                File.WriteAllText(Path.Combine(d, "part-0.log"), "x");
            }
            // 活跃目录 = 测试内持锁模拟（本进程持锁与「他实例持锁」由同一判据覆盖）
            using var active = SpoolPaths.NewSessionDir(root);
            File.WriteAllText(Path.Combine(active.Dir, "part-0.log"), "x");

            var (deleted, skipped, failed) = FileProxyService.CleanupStaleSpoolDirs(root);

            Assert.Equal(2, deleted);
            Assert.Equal(1, skipped);
            Assert.Equal(0, failed);
            Assert.False(Directory.Exists(dead1));
            Assert.False(Directory.Exists(dead2));
            Assert.True(Directory.Exists(active.Dir));   // 活性锁持有的目录不误删
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void 启动清理_无子目录时零操作()
    {
        var root = Path.Combine(TempRoot.Dir, "cleanup-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (deleted, skipped, failed) = FileProxyService.CleanupStaleSpoolDirs(root);
            Assert.Equal((0, 0, 0), (deleted, skipped, failed));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void 启动清理_根目录不存在时零操作不抛()
    {
        var root = Path.Combine(TempRoot.Dir, "cleanup-gone-" + Guid.NewGuid().ToString("N"));
        var (deleted, skipped, failed) = FileProxyService.CleanupStaleSpoolDirs(root);
        Assert.Equal((0, 0, 0), (deleted, skipped, failed));
    }

    [Fact]
    public void 启动清理_删不掉只留痕不抛()
    {
        var root = Path.Combine(TempRoot.Dir, "cleanup-locked-" + Guid.NewGuid().ToString("N"));
        var victim = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(victim);
        // 独占句柄锁住目录内文件 → Directory.Delete 必失败（无锁文件，活性探测判无主）
        var lockedFile = Path.Combine(victim, "locked.bin");
        using (var fs = new FileStream(lockedFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var (deleted, skipped, failed) = FileProxyService.CleanupStaleSpoolDirs(root);
            Assert.Equal(0, deleted);
            Assert.Equal(1, failed);   // 只留痕不抛
            Assert.Equal(0, skipped);
            Assert.True(Directory.Exists(victim));
        }
        // 句柄释放后残留目录仍在（留痕语义：留给下次启动清理，不在本调用内硬删）
        Assert.True(Directory.Exists(victim));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void 启动清理_混合场景计数正确()
    {
        var root = Path.Combine(TempRoot.Dir, "cleanup-mixed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dead = Path.Combine(root, "dead");
            Directory.CreateDirectory(dead);                       // 无锁 → 删
            using var active = SpoolPaths.NewSessionDir(root);      // 持锁 → 跳过
            var locked = Path.Combine(root, "locked");
            Directory.CreateDirectory(locked);                      // 无锁但内容被占 → 删除失败
            var lockFs = new FileStream(Path.Combine(locked, "l.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var (deleted, skipped, failed) = FileProxyService.CleanupStaleSpoolDirs(root);
                Assert.Equal(1, deleted);
                Assert.Equal(1, skipped);
                Assert.Equal(1, failed);
                Assert.True(Directory.Exists(active.Dir));
            }
            finally { lockFs.Dispose(); }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
