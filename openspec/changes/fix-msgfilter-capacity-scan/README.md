# fix-msgfilter-capacity-scan

MsgFilter 大小写不敏感路径按 scratch 容量逐行扫描导致网络/本地文件 message 过滤极慢；修复为按行内字节切片折叠（纯 bug+性能修复，行为不变）
