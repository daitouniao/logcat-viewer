namespace logcat.Services;

/// <summary>英文译文表：文件面板、文件浏览、传输。</summary>
static partial class LocTable
{
    public static Dictionary<string, string> FileUi() => new Dictionary<string, string>()
    {
        // ── 面板栏与按钮 ──
        ["收藏:"] = "Favorites:",
        ["★ 收藏当前目录"] = "★ Favorite This Folder",
        ["★ 已收藏"] = "★ Favorited",
        ["☆ 取消收藏"] = "☆ Unfavorite",
        ["路径:"] = "Path:",
        ["前往"] = "Go",
        ["上级"] = "Up",
        ["⬅ 下载到本机"] = "⬅ Download to PC",
        ["上传到设备 ➡"] = "Upload to Device ➡",
        ["上传文件到此处…"] = "Upload Files Here…",
        ["➡ 上传到设备"] = "➡ Upload to Device",
        ["在资源管理器打开"] = "Open in Explorer",
        ["刷新两栏"] = "Refresh Both Panes",

        // ── 列表列头 ──
        ["名称"] = "Name",
        ["大小"] = "Size",
        ["类型"] = "Type",
        ["权限"] = "Permissions",
        ["所有者"] = "Owner",
        ["修改日期"] = "Modified",

        // ── 面板标题 ──
        ["本机电脑"] = "This PC",
        ["Android 设备 — {0}{1}"] = "Android device — {0}{1}",
        ["文件浏览 — {0}{1}"] = "File Browser — {0}{1}",
        ["此电脑"] = "This PC",
        ["我的电脑"] = "My Computer",

        // ── 文件类型 ──
        ["文件夹"] = "Folder",
        ["文件"] = "File",
        ["{0} 文件"] = "{0} file",
        ["本地磁盘"] = "Local Disk",
        ["可移动磁盘"] = "Removable Disk",
        ["光盘驱动器"] = "Optical Drive",
        ["网络驱动器"] = "Network Drive",
        ["RAM 磁盘"] = "RAM Disk",
        ["驱动器"] = "Drive",
        ["未就绪"] = "Not Ready",

        // ── 右键菜单 ──
        ["复制完整路径"] = "Copy Full Path",
        ["复制文件名"] = "Copy File Name",
        ["跳转到该目录"] = "Jump to Folder",
        ["取消收藏"] = "Remove Favorite",
        ["重命名备注…"] = "Rename Remark…",
        ["收藏备注"] = "Favorite Remark",

        // ── 收藏下拉 ──
        ["— 收藏目录（{0}）—"] = "— Favorite folders ({0}) —",
        ["请先在下拉框中选中一个收藏目录，再右键取消收藏。\n（也可以直接用左侧的「☆ 取消收藏」按钮移除当前目录。）"] =
            "Select a favorite folder from the dropdown first, then right-click to remove it.\n" +
            "(You can also use the \"☆ Unfavorite\" button on the left to remove the current folder.)",
        ["确定要清空{0}的全部收藏目录吗？"] = "Clear all {0} favorite folders?",
        ["该目录未被收藏"] = "That folder is not favorited",
        ["已取消收藏：{0}"] = "Removed from favorites: {0}",
        ["为收藏目录设置备注名（留空则直接显示路径）：\n{0}"] =
            "Set a remark name for this favorite folder (leave empty to show the path):\n{0}",

        // ── 重命名 / 新建校验 ──
        ["输入新名称：\n{0}"] = "Enter a new name:\n{0}",
        ["名称不能为空。"] = "Name cannot be empty.",
        ["名称包含非法字符。"] = "Name contains invalid characters.",
        ["不能使用 . 或 .. 作为名称。"] = "Cannot use . or .. as a name.",
        ["当前目录已存在名为「{0}」的项。"] = "An item named \"{0}\" already exists in this folder.",
        ["请先进入一个具体目录，再新建文件夹。"] = "Enter a folder first, then create a new folder.",
        ["目标已存在：{0}"] = "Target already exists: {0}",

        // ── 列表状态 ──
        ["{0} 项"] = "{0} items",
        ["已复制 {0} 个路径"] = "Copied {0} paths",
        ["重命名 {0} → {1}…"] = "Renaming {0} → {1}…",
        ["已重命名为 {0}"] = "Renamed to {0}",
        ["重命名失败：{0}"] = "Rename failed: {0}",
        ["创建失败：{0}"] = "Create failed: {0}",
        ["删除 {0}…"] = "Deleting {0}…",
        ["删除 {0} 失败"] = "Failed to delete {0}",
        ["重命名 {0} 失败"] = "Failed to rename {0}",
        ["已删除 {0} 项"] = "Deleted {0} items",
        ["删除失败：{0}"] = "Delete failed: {0}",
        ["{0} 项删除失败：\n{1}"] = "Failed to delete {0} items:\n{1}",
        ["无法打开 {0}\n\n{1}"] = "Cannot open {0}\n\n{1}",

        // ── 权限错误 ──
        ["无法访问 {0}，权限不足或目录不存在{1}"] = "Cannot access {0}: permission denied or directory not found{1}",
        ["无法访问 {0}，权限不足或目录不存在"] = "Cannot access {0}: permission denied or directory not found",
        ["无法创建 {0}"] = "Cannot create {0}",
        ["无法读取 {0}"] = "Cannot read {0}",
        ["无法写入 {0}"] = "Cannot write {0}",
        ["设备未返回应用列表（可能无三方应用或权限不足）"] =
            "The device returned no app list (possibly no third-party apps, or insufficient permission)",

        // ── 传输 ──
        ["请先在右侧本机面板选中要上传的文件"] = "Select files to upload in the right (PC) pane first",
        ["请先在左侧设备面板选中要下载的文件"] = "Select files to download in the left (device) pane first",
        ["请先在左侧设备面板进入目标目录"] = "Enter a target folder in the left (device) pane first",
        ["请先在右侧本机面板进入目标目录"] = "Enter a target folder in the right (PC) pane first",
        ["请先进入一个本机目录"] = "Enter a local folder first",
        ["{0}: 目录无法递归展开（设备可能不支持 find，或目录为空/不可读）"] =
            "{0}: folder cannot be expanded recursively (the device may not support find, or the folder is empty/unreadable)",
        ["({0}/{1}) 正在 sync 刷写到存储…"] = "({0}/{1}) syncing to storage…",
        ["传输完成：成功 {0} 个{1}"] = "Transfer complete: {0} succeeded{1}",
        ["复制完成：成功 {0} 个{1}"] = "Copy complete: {0} succeeded{1}",
        ["，失败 {0} 个"] = ", {0} failed",
        ["{0} 个文件复制失败：\n{1}"] = "{0} files failed to copy:\n{1}",
        ["文件正在传输中，请等待完成后再关闭。"] = "Files are still being transferred; please wait before closing.",
        // 键含箭头前缀：调用点是 Loc.F("⬅ 下载到 {0}", ...)，箭头必须一起进键
        ["⬅ 下载到 {0}"] = "⬅ Download to {0}",
        ["上传到 {0} ➡"] = "Upload to {0} ➡",

        // ── OpenFileDialog过滤器（英文态下 "所有文件" 会露中文）──
        ["所有文件 (*.*)|*.*"] = "All files (*.*)|*.*",

        // ── 本机端重命名 ──
        ["目标已存在：{newName}"] = "Target already exists: {newName}",

        // ── 端切换标签 ──
        ["设备端"] = "Device",
        ["本机端"] = "Local",

        // ── 收藏目录下拉框的提示行 ──
        ["— 暂无收藏目录 —"] = "— No favorites yet —",

        // ── 批量选择超限提示 ──
        // 键含前导空格：调用点是 `names += Loc.F(" …等 {0} 项", n)`，
        // 空格是拼接分隔符，必须一起进键，否则查不到译文
        [" …等 {0} 项"] = " … and {0} more",
    };
}