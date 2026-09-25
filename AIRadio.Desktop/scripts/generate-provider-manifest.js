// 在构建时从锁文件生成随包组件清单，不联网，也不读取用户凭据。
const fs = require('node:fs');
const path = require('node:path');

const output = process.argv[2];
if (!output) throw new Error('Missing output path');

const providers = [
  { id: 'netease-node', directory: 'server', source: 'npm:NeteaseCloudMusicApi@4.32.0' },
  {
    id: 'kugou-node', directory: 'server-kugou',
    source: 'https://github.com/MakcRe/KuGouMusicApi/commit/bcf4b8af1c4c514b8c15fc1233b03d5b4377aab5',
  },
];
const root = path.resolve(__dirname, '..');
const manifest = {
  schemaVersion: 1,
  generatedFrom: 'checked-in package-lock.json',
  providers: providers.map(provider => {
    const pkg = JSON.parse(fs.readFileSync(path.join(root, provider.directory, 'package.json'), 'utf8'));
    const lock = JSON.parse(fs.readFileSync(path.join(root, provider.directory, 'package-lock.json'), 'utf8'));
    const components = Object.entries(lock.packages || {})
      .filter(([name, info]) => name.startsWith('node_modules/') && !info.dev)
      .map(([name, info]) => ({
        name: name.slice('node_modules/'.length),
        version: info.version || '',
        license: info.license || 'see package metadata',
        source: info.resolved || '',
        integrity: info.integrity || '',
      }))
      .sort((a, b) => a.name.localeCompare(b.name));
    return {
      id: provider.id,
      version: pkg.version,
      license: pkg.license || 'see upstream package',
      source: provider.source,
      components,
    };
  }),
};
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify(manifest, null, 2) + '\n');
