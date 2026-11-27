/**
 * 测试用的假上游：以标准输入输出承载的最小 MCP 服务端。
 *
 * 用真实协议（而非打桩的传输层）做测试，断言的才是线上真实字节。
 * 通过命令行参数切换行为：
 *   --tools a,b,c   暴露哪些工具
 *   --fail <tool>   该工具返回失败结果
 *   --slow <tool>   该工具永不返回（用于超时用例）
 *   --weird         额外暴露名字不合规的工具
 *   --noise <text>  往标准错误写一行
 */
import { writeFileSync } from 'node:fs';

import { Server } from '@modelcontextprotocol/server';
import type { Tool } from '@modelcontextprotocol/server';
import { StdioServerTransport } from '@modelcontextprotocol/server/stdio';

function argValue(name: string): string | undefined {
  const index = process.argv.indexOf(`--${name}`);
  if (index >= 0) {
    return process.argv[index + 1];
  }
  const inline = process.argv.find((item) => item.startsWith(`--${name}=`));
  return inline?.slice(name.length + 3);
}

const toolNames = (argValue('tools') ?? 'echo').split(',').filter((name) => name.length > 0);
const failTool = argValue('fail');
const slowTool = argValue('slow');
const noise = argValue('noise');
const pidFile = argValue('pid-file');

const server = new Server({ name: 'fake-upstream', version: '0.0.1' });
server.registerCapabilities({ tools: { listChanged: false } });

server.setRequestHandler('tools/list', async () => {
  const tools: Tool[] = toolNames.map((name) => ({
    name,
    description: `fake tool ${name}`,
    inputSchema: {
      type: 'object' as const,
      properties: { text: { type: 'string' } },
      required: ['text'],
    },
  }));

  if (process.argv.includes('--weird')) {
    tools.push({
      name: 'weird name',
      description: 'tool with an illegal name',
      inputSchema: { type: 'object', properties: {}, required: [] },
    });
    tools.push({
      name: 'read__file',
      description: 'tool whose name contains the separator',
      inputSchema: { type: 'object', properties: {}, required: [] },
    });
  }

  return { tools };
});

server.setRequestHandler('tools/call', async (request) => {
  const name = request.params.name;
  if (slowTool !== undefined && name === slowTool) {
    await new Promise<never>(() => {
      // 永不返回：用于验证调用时限。
    });
  }
  if (failTool !== undefined && name === failTool) {
    return {
      isError: true,
      content: [{ type: 'text' as const, text: 'upstream failure detail' }],
    };
  }
  return {
    content: [
      {
        type: 'text' as const,
        text: `called ${name} with ${JSON.stringify(request.params.arguments ?? {})}`,
      },
    ],
  };
});

if (noise !== undefined) {
  console.error(noise);
}

if (pidFile !== undefined) {
  // 供进程树清理用例核对：上游把自己的 pid 写出来。
  writeFileSync(pidFile, String(process.pid), 'utf8');
}

await server.connect(new StdioServerTransport());
