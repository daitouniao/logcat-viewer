/**
 * 终止信号处理。
 *
 * 把「收到信号 → 写诊断 → 退出」这段逻辑从入口脚本里抽出来，好处有二：
 * 一是入口脚本只剩接线；二是不必依赖「父进程能否给子进程投递信号」这一
 * 平台差异极大的手段，也能对收尾行为本身做直接断言。
 *
 * 注意：诊断只写标准错误，且先写诊断再退出——退出后不再向标准输出写任何内容。
 */

export interface ShutdownOptions {
  /** 写出诊断信息（调用方应指向标准错误）。 */
  report(message: string): void;
  /** 结束进程。 */
  exit(code: number): void;
}

/**
 * 生成信号处理器。
 *
 * 处理器只做两件事：写一条诊断、退出。刻意不在退出前做任何清理动作——
 * 传输层的关闭由 stdin EOF 路径负责，信号路径只保证「不再写 stdout」。
 */
export function createShutdownHandler(options: ShutdownOptions): (signal: string) => void {
  return (signal: string): void => {
    options.report(`[logcat-mcp] received ${signal}, shutting down`);
    options.exit(0);
  };
}

/**
 * 注册终止信号处理器，返回注销函数。
 *
 * SIGINT / SIGTERM 在各平台通用；SIGBREAK 是 Windows 控制台 Ctrl+Break 的
 * 对应信号，POSIX 上不存在，故只在 Windows 注册。
 */
export function installShutdownHandlers(options: ShutdownOptions): () => void {
  const handler = createShutdownHandler(options);
  const signals: NodeJS.Signals[] = ['SIGINT', 'SIGTERM'];
  if (process.platform === 'win32') {
    signals.push('SIGBREAK');
  }

  for (const signal of signals) {
    process.on(signal, handler);
  }

  return () => {
    for (const signal of signals) {
      process.off(signal, handler);
    }
  };
}
