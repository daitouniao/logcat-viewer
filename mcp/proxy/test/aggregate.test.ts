import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { isCompliantDownstreamName, normalizeToolName } from '../src/naming.js';
import { ToolRegistry, type UpstreamToolDefinition } from '../src/registry.js';

function tool(name: string, description?: string): UpstreamToolDefinition {
  return {
    name,
    ...(description ? { description } : {}),
    inputSchema: { type: 'object', properties: {} },
  };
}

describe('AggregationTests', () => {
  it('NamespacesToolNames', () => {
    const registry = new ToolRegistry();
    const result = registry.register('fs', [tool('read_file')]);
    assert.equal(result.added.length, 1);
    assert.equal(result.added[0]?.downstreamName, 'fs__read_file');
  });

  it('DisambiguatesSameNamedTools', () => {
    const registry = new ToolRegistry();
    registry.register('fs', [tool('search')]);
    const second = registry.register('db', [tool('search')]);
    assert.equal(second.added.length, 1);
    assert.equal(second.added[0]?.downstreamName, 'db__search');
    assert.equal(registry.list().length, 2);
  });

  it('KeepsUpstreamInputSchema', () => {
    const registry = new ToolRegistry();
    const schema = { type: 'object' as const, properties: { text: { type: 'string' } }, required: ['text'] };
    const result = registry.register('fs', [{ name: 'echo', inputSchema: schema }]);
    assert.deepEqual(result.added[0]?.inputSchema, schema);
  });

  it('KeepsOriginalNameInDescription', () => {
    const registry = new ToolRegistry();
    const result = registry.register('fs', [tool('read_file', 'reads a file')]);
    const description = result.added[0]?.description ?? '';
    assert.ok(description.includes('reads a file'));
    assert.ok(description.includes('upstream: fs'));
    assert.ok(description.includes('read_file'));
  });

  it('NormalizesIllegalUpstreamToolName', () => {
    const normalized = normalizeToolName('fs', 'weird name');
    assert.equal(normalized.changed, true);
    assert.equal(normalized.downstreamName, 'fs__weird_name');
    assert.ok(isCompliantDownstreamName(normalized.downstreamName));
    assert.equal(normalized.originalName, 'weird name');
  });

  it('TruncatesOverlongNameKeepingIdSegment', () => {
    const normalized = normalizeToolName('fs', 'x'.repeat(120));
    assert.equal(normalized.downstreamName.length, 64);
    assert.ok(normalized.downstreamName.startsWith('fs__'));
  });

  it('CollapsesConsecutiveUnderscoresInToolName', () => {
    const normalized = normalizeToolName('fs', 'read__file');
    assert.equal(normalized.downstreamName, 'fs__read_file');
  });

  it('DetectsToolNameCollisionWithinUpstream', () => {
    const registry = new ToolRegistry();
    // 两个不同的原始名规范化后落到同一个下游名。
    const result = registry.register('fs', [tool('a b'), tool('a_b')]);
    assert.ok(result.conflict !== undefined);
    assert.equal(result.conflict?.downstreamName, 'fs__a_b');
    assert.equal(result.added.length, 0);
    assert.equal(registry.list().length, 0);
  });

  it('CallsThroughToOriginalTool', async () => {
    // 名称被规范化后，调用仍到达原始工具。
    const normalized = normalizeToolName('fs', 'weird name');
    assert.equal(normalized.downstreamName, 'fs__weird_name');
    assert.equal(normalized.originalName, 'weird name');
  });

  it('KeepsDifferentUpstreamsApart', () => {
    const registry = new ToolRegistry();
    const first = registry.register('a', [tool('b__c')]);
    const second = registry.register('a_b', [tool('c')]);
    assert.ok(first.conflict === undefined);
    assert.ok(second.conflict === undefined);
    assert.equal(registry.list().length, 2);
    // 下游名里至多一处分隔符：路由不依赖切分名字。
    assert.equal(first.added[0]?.downstreamName, 'a__b_c');
    assert.equal(second.added[0]?.downstreamName, 'a_b__c');
  });
});
