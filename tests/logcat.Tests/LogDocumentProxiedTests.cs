using System;
using System.IO;
using System.Text;
using logcat.Services;
using Xunit;

namespace logcat.Tests;

/// <summary>
/// LogDocument 显示路径/源路径分离与代理文档语义（任务 5.1，spec「文档路径语义」）：
/// 构造/构建入口可选显示路径参数（默认等于路径）、代理布尔字段、暂存目录字段（默认 null）、
/// Close 自删暂存目录（删不掉只留痕）、非代理文档 Close 零行为变化。
///
/// 「7 处文件触及点改源路径」用「显示路径指向打不开的路径」间接验证：
/// 若任一触及点（打开映射、重载头部探测、增量回退、两处文件长度查询、两处扫描传参）
/// 误按显示路径开文件，对应用例会以 FileNotFoundException 失败——漏改一处即红。
/// </summary>
public class LogDocumentProxiedTests
{
    /// <summary>模拟受限路径：GetFullPath 可处理（仅字符串操作），但文件系统上不存在。</summary>
    const string UnreadableDisplay = @"Z:\no-access\原始受限路径.log";

    [Fact]
    public void 默认显示路径等于源路径()
    {
        using var tmp = new TempLogFile("09-23 18:00:00.000 E Tag crash\n");
        using var doc = LogDocument.Build(tmp.Path);

        Assert.Equal(tmp.Path, doc.Path);            // Path（显示）= 源
        Assert.Equal(tmp.Path, doc.SourcePath);      // 两者相等（非代理文档）
        Assert.False(doc.IsProxied);                 // 代理布尔默认 false
        Assert.Null(doc.SpoolDir);                   // 暂存目录默认 null
    }

    [Fact]
    public void 显示路径指定时两路径分离()
    {
        using var tmp = new TempLogFile("09-23 18:00:00.000 E Tag crash\n");
        using var doc = LogDocument.Build(tmp.Path, displayPath: UnreadableDisplay);

        Assert.Equal(UnreadableDisplay, doc.Path);   // 显示 = 用户原路径
        Assert.Equal(tmp.Path, doc.SourcePath);      // 源 = 实际读取文件
        Assert.NotEqual(doc.Path, doc.SourcePath);
    }

    [Fact]
    public void 代理文档_内部按源路径读文件_显示路径无需存在()
    {
        // 显示路径指向不存在的受限路径：打开映射 / 两处长度查询 / 扫描传参全按源路径走
        var line1 = "09-23 18:00:00.000 E Tag crash part 1\n";
        var line2 = "09-23 18:00:01.000 E Tag crash part 2\n";
        using var tmp = new TempLogFile(line1 + line2);
        using var doc = LogDocument.Build(tmp.Path, displayPath: UnreadableDisplay);

        Assert.Equal(2, doc.RowCount);
        Assert.Equal(tmp.Length, doc.Size);          // 长度查询走源路径
        Assert.Contains("crash part 1", Encoding.UTF8.GetString(doc.LineBytes(0)));
        Assert.Contains("crash part 2", Encoding.UTF8.GetString(doc.LineBytes(1)));
    }

    [Fact]
    public void 代理文档_Reload按源路径增量()
    {
        // Reload 的重载头部探测、增量回退（TrimPartial）、文件长度查询、扫描传参全按源路径走
        var line1 = "09-23 18:00:00.000 E Tag first\n";
        var line2 = "09-23 18:00:01.000 E Tag second\n";
        using var tmp = new TempLogFile(line1);
        using var doc = LogDocument.Build(tmp.Path, displayPath: UnreadableDisplay);
        Assert.Equal(1, doc.RowCount);

        File.AppendAllText(tmp.Path, line2, Encoding.UTF8);   // 源文件（暂存文件）追加
        doc.Reload();

        Assert.Equal(2, doc.RowCount);               // 增量读到了追加行
        Assert.Equal(UnreadableDisplay, doc.Path);   // 显示路径不变
        Assert.Contains("Tag second", Encoding.UTF8.GetString(doc.LineBytes(1)));
    }

    [Fact]
    public void Close_删暂存目录()
    {
        var line = "09-23 18:00:00.000 E Tag x\n";
        using var tmp = new TempLogFile(line);
        var spoolDir = Path.Combine(TempRoot.Dir, "proxd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        File.WriteAllText(Path.Combine(spoolDir, "part-0.log"), "data");

        var doc = LogDocument.Build(tmp.Path, displayPath: UnreadableDisplay);
        doc.IsProxied = true;
        doc.SpoolDir = spoolDir;
        doc.Close();

        Assert.False(Directory.Exists(spoolDir));    // 既有释放序列之后自删暂存目录
    }

    [Fact]
    public void Close_删不掉只留痕不抛()
    {
        var line = "09-23 18:00:00.000 E Tag x\n";
        using var tmp = new TempLogFile(line);
        var spoolDir = Path.Combine(TempRoot.Dir, "proxd-locked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDir);
        using var lockFs = new FileStream(Path.Combine(spoolDir, "locked.bin"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var doc = LogDocument.Build(tmp.Path, displayPath: UnreadableDisplay);
        doc.IsProxied = true;
        doc.SpoolDir = spoolDir;
        doc.Close();                                 // 不抛：删不掉只留痕（StartupLog 记录）

        Assert.True(Directory.Exists(spoolDir));
        lockFs.Dispose();
        try { Directory.Delete(spoolDir, recursive: true); } catch { }
    }

    [Fact]
    public void 非代理文档Close_零行为变化()
    {
        var line = "09-23 18:00:00.000 E Tag x\n";
        using var tmp = new TempLogFile(line);
        var doc = LogDocument.Build(tmp.Path);
        Assert.Null(doc.SpoolDir);
        doc.Close();                                 // 无暂存目录：不做任何目录操作
        Assert.True(File.Exists(tmp.Path));          // 原文件不动
        Assert.False(doc.IsProxied);
    }
}
