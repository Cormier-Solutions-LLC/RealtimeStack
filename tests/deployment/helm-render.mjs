import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../../', import.meta.url));

// Use Helm's own YAML parser to inspect resources without a cluster or a second parser dependency.
export function renderChart(helm, values, { release = 'example', namespace = 'example-realtime', yamlOnly = false } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'realtimestack-chart-test-'));
  try {
    fs.writeFileSync(path.join(dir, 'values.json'), JSON.stringify(values));
    const yaml = execFileSync(helm, ['template', release, path.join(root, 'helm/realtimestack-gateway'), '-n', namespace, '-f', path.join(dir, 'values.json')], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    if (yamlOnly) return yaml;
    fs.mkdirSync(path.join(dir, 'parser/templates'), { recursive: true });
    fs.writeFileSync(path.join(dir, 'parser/Chart.yaml'), 'apiVersion: v2\nname: parser\nversion: 0.1.0\n');
    fs.writeFileSync(path.join(dir, 'parser/resources.yaml'), yaml);
    fs.writeFileSync(path.join(dir, 'parser/templates/json.yaml'), '{{ range $doc := splitList "\\n---\\n" (.Files.Get "resources.yaml") }}{{ if trim $doc }}{{ $object := fromYaml $doc }}{{ if hasKey $object "Error" }}{{ fail $object.Error }}{{ end }}{{ $object | toJson }}\n{{ end }}{{ end }}');
    const json = execFileSync(helm, ['template', 'parse', path.join(dir, 'parser')], { encoding: 'utf8' });
    return json.split('\n').filter(line => line.startsWith('{')).map(line => JSON.parse(line));
  } finally {
    if (path.dirname(dir) !== os.tmpdir() || !path.basename(dir).startsWith('realtimestack-chart-test-')) throw Error('Unexpected fixture cleanup path');
    fs.rmSync(dir, { recursive: true, force: true });
  }
}
