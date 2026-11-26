namespace Next.App.Storage;

/// <summary>目录可写探测（design D2）：做成接口以便测试注入，不依赖真实 ACL。</summary>
public interface IDirectoryProbe
{
    bool CanWrite(string directory);
}

/// <summary>真实探测：建目录、写临时文件、删除；任何异常视为不可写。</summary>
public sealed class FileSystemProbe : IDirectoryProbe
{
    public bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
