import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import vm from 'node:vm';
import { createHarness } from './i18n-dom.mjs';

const root = new URL('../', import.meta.url);
const read = path => readFile(new URL(path, root), 'utf8');
const source = await read('web/i18n.js');
const html = await read('web/index.html');
const match = source.match(/const dictionaries = (\{[\s\S]*?\n\});/);
assert.ok(match, 'dictionary must be a JSON object literal');
const dictionaries = JSON.parse(match[1]);
const languages = ['zh-Hant', 'ja', 'en'];
let passed = 0;
let failed = 0;
async function test(name, run) {
  try { await run(); passed++; console.log('PASS ' + name); }
  catch (error) { failed++; console.error('FAIL ' + name + ' ' + error.stack); }
}
function stripComments(source) {
  let result = '';
  let quote = null;
  for (let i = 0; i < source.length; i++) {
    const character = source[i];
    if (quote) {
      result += character;
      if (character === '\\') { result += source[++i] || ''; continue; }
      if (character === quote) quote = null;
    } else if (['"', "'", String.fromCharCode(96)].includes(character)) { quote = character; result += character; }
    else if (source.slice(i, i + 2) === '//') {
      const end = source.indexOf('\n', i);
      if (end < 0) break;
      i = end - 1;
    } else if (source.slice(i, i + 2) === '/*') {
      const end = source.indexOf('*/', i + 2);
      i = end < 0 ? source.length : end + 1;
    } else result += character;
  }
  return result;
}
const parameters = value => [...value.matchAll(/\{(\w+)\}/g)].map(item => item[1]).sort();
function harness(options) { const h = createHarness(html, options); h.run(source, 'i18n.js'); h.ready(); return h; }
await test('dictionary keys and nonempty values', () => {
  assert.deepEqual(Object.keys(dictionaries).sort(), [...languages].sort());
  const keys = Object.keys(dictionaries['zh-Hant']).sort();
  for (const language of languages) {
    assert.deepEqual(Object.keys(dictionaries[language]).sort(), keys);
    for (const [key, value] of Object.entries(dictionaries[language])) assert.ok(typeof value === 'string' && value.trim(), key);
  }
});
await test('matching parameter placeholders', () => {
  for (const key of Object.keys(dictionaries.en))
    for (const language of languages) assert.deepEqual(parameters(dictionaries[language][key]), parameters(dictionaries['zh-Hant'][key]), key);
});
await test('no CJK literals outside the dictionary', async () => {
  for (const file of (await readdir(new URL('web/', root))).filter(name => name.endsWith('.js') && name !== 'i18n.js')) {
    const original = await read('web/' + file);
    assert.doesNotMatch(stripComments(original), /[\u3040-\u30ff\u3400-\u9fff]/u, file);
    new vm.Script(original, { filename: file });
  }
  assert.doesNotMatch(html.replace(/<!--[\s\S]*?-->/g, '').replace(/\/\*[\s\S]*?\*\//g, ''), /[\u3040-\u30ff\u3400-\u9fff]/u);
});
await test('all literal translation references exist', async () => {
  for (const file of ['web/app.js', 'web/files.js', 'web/realtime.js']) {
    for (const match of (await read(file)).matchAll(/\bt\("([^"]+)"/g))
      assert.ok(Object.hasOwn(dictionaries.en, match[1]) || Object.hasOwn(dictionaries.en, match[1] + '.other'), match[1]);
  }
  for (const match of html.matchAll(/data-i18n(?:-[\w-]+)?="([^"]+)"/g)) assert.ok(Object.hasOwn(dictionaries.en, match[1]), match[1]);
});
await test('backend error codes all have translations', async () => {
  const codes = new Set();
  for (const file of ['src/http.rs', 'src/files.rs', 'src/realtime.rs']) {
    const text = await read(file);
    for (const expression of [/(?:error|failure)\(\s*\d+\s*,\s*"([a-z_]+)"/g,
      /file_error\([^,]+,\s*\d+\s*,\s*"([a-z_]+)"/g,
      /\(\s*\d+\s*,\s*"([a-z_]+)"\s*,/g, /"code"\s*:\s*"([a-z_]+)"/g])
      for (const match of text.matchAll(expression)) codes.add(match[1]);
  }
  assert.ok(codes.size > 40);
  for (const code of codes) for (const language of languages) assert.ok(dictionaries[language]['error.' + code], code);
  console.log('HTTP error codes=' + codes.size);
});
await test('browser detection and stored preference', () => {
  for (const [language, expected] of [['ja', 'ja'], ['ja-JP', 'ja'], ['en-US', 'en'], ['en', 'en'],
    ['zh-TW', 'zh-Hant'], ['zh-CN', 'zh-Hant'], ['zh', 'zh-Hant'], ['fr-FR', 'zh-Hant'], ['', 'zh-Hant'], [undefined, 'zh-Hant']])
    assert.equal(harness({ language }).window.I18n.getLanguage(), expected);
  assert.equal(harness({ language: 'en', stored: 'ja' }).window.I18n.getLanguage(), 'ja');
  assert.equal(harness({ language: 'en', stored: 'fr' }).window.I18n.getLanguage(), 'en');
});
await test('storage exceptions and invalid language are safe', () => {
  const h = harness({ language: 'ja-JP', storageThrows: true });
  assert.equal(h.window.I18n.getLanguage(), 'ja');
  assert.equal(h.window.I18n.setLanguage('en'), true);
  assert.equal(h.window.I18n.setLanguage('fr'), false);
  assert.equal(h.document.documentElement.lang, 'en');
});

await test('static title aria placeholder and selector switch live', () => {
  const h = harness({ language: 'en' });
  const title = h.document.documentElement.children.find(element => element.tagName === 'HEAD').children.find(element => element.tagName === 'TITLE');
  assert.match(title.textContent, /Type on your phone/);
  const input = h.document.getElementById('editor');
  const select = h.document.getElementById('languageSelect');
  select.value = 'ja'; select.dispatchEvent({ type: 'change' });
  assert.match(title.textContent, /スマートフォン/);
  assert.match(input.getAttribute('placeholder'), /ここに入力/);
  assert.equal(select.getAttribute('aria-label'), '言語');
  assert.equal(h.storage.get('fastinput.language'), 'ja');
  assert.equal(h.sent.length, 1);
});
await test('native bridge validates language and prevents loops', () => {
  const h = harness({ language: 'en' });
  h.native.dispatchEvent({ type: 'message', data: { type: 'language.set', lang: 'ja' } });
  assert.equal(h.window.I18n.getLanguage(), 'ja');
  assert.equal(h.sent.length, 0);
  for (const data of [null, {}, { type: 'language.set', lang: 'fr' }, { type: 'other', lang: 'en' }, { type: 'language.set', lang: 123 }])
    h.native.dispatchEvent({ type: 'message', data });
  assert.equal(h.window.I18n.getLanguage(), 'ja');
});
await test('parameters plurals numbers and capacities', () => {
  const { window: { I18n: i18n } } = harness({ language: 'en' });
  assert.equal(i18n.t('common.character', { count: 1 }), '1 character');
  assert.equal(i18n.t('common.character', { count: 1234 }), '1,234 characters');
  assert.equal(i18n.t('common.file', { count: 1 }), '1 file');
  assert.equal(i18n.bytes(1536), '1.5 KB');
  assert.equal(i18n.bytes(0), '0 B');
  assert.equal(i18n.bytes(Number.NaN), '0 B');
  i18n.setLanguage('ja');
  assert.equal(i18n.t('common.character', { count: 1 }), '1 文字');
  assert.equal(i18n.t('missing.key'), 'missing.key');
  const error = i18n.t('error.file_too_large');
  const composed = i18n.t('files.upload.failed', { value0: i18n.ref(error) });
  i18n.setLanguage('en');
  assert.equal(i18n.localize(composed), 'Upload failed. ' + dictionaries.en['error.file_too_large']);
});
await test('error codes take precedence and unknown codes preserve messages', () => {
  const i18n = harness({ language: 'en' }).window.I18n;
  assert.equal(i18n.errorMessage({ code: 'not_paired', message: 'fallback' }), dictionaries.en['error.not_paired']);
  assert.equal(i18n.errorMessage({ code: 'future_unknown', message: 'fallback' }), 'fallback');
  assert.equal(i18n.errorMessage({ code: 'future_unknown', error: 'legacy fallback' }), 'legacy fallback');
});
await test('transient bindings switch and preserve user input', () => {
  const h = harness({ language: 'en' });
  const message = h.window.I18n.t('error.file_too_large');
  h.window.I18n.t('realtime.start.real.time.typing');
  const filename = 'Start real-time typing';
  const link = h.document.createElement('a');
  h.document.body.append(link);
  h.window.I18n.bindAttribute(link, 'aria-label', () => h.window.I18n.t('files.download', { value0: filename }));
  const toast = h.document.getElementById('toast');
  h.window.I18n.bind(toast, 'textContent', () => message);
  const editor = h.document.getElementById('editor');
  editor.value = 'User text 日本語 繁中';
  h.window.I18n.setLanguage('ja');
  assert.equal(toast.textContent, dictionaries.ja['error.file_too_large']);
  assert.equal(link.getAttribute('aria-label'), filename + ' をダウンロード');
  assert.equal(editor.value, 'User text 日本語 繁中');
});
await test('all application scripts initialize and rerender in a DOM stub', async () => {
  const h = createHarness(html, { language: 'en' });
  h.responses.set('/api/state', { status: 200, data: { role: 'desktop', status: 'ready', text: '', revision: 0, generation: 'test',
    pairing: { pin: '123456', expires_seconds: 120 }, pending: [{ id: "pending", ip: "127.0.0.1", code: "654321" }], devices: [{ id: "device", label: "測試手機", last_seen: 1700000000 }] } });
  h.responses.set('/api/files', { status: 200, data: { files: [{ id: "0123456789abcdef", name: "資料.txt", size: 1536, source: "phone", expires_at: Math.floor(Date.now() / 1000) + 3600 }], max_file_bytes: 209715200, max_total_bytes: 2147483648, max_files: 200, ttl_seconds: 86400 } });
  for (const file of ['web/i18n.js', 'web/files.js', 'web/realtime.js', 'web/app.js']) h.run(await read(file), file);
  h.ready(); await h.settle();
  const editor = h.document.getElementById('editor');
  editor.value = 'Keep this text'; editor.setSelectionRange(2, 5);
  for (const language of ['ja', 'zh-Hant', 'en']) { h.window.I18n.setLanguage(language); await h.settle(); }
  assert.equal(editor.value, 'Keep this text');
  assert.match(h.document.getElementById('pendingList').textContent, /Approve/);
  assert.match(h.document.getElementById('deviceList').textContent, /測試手機/);
  assert.match(h.document.getElementById('filesList').textContent, /Download/);
  assert.match(h.document.getElementById('filesList').textContent, /資料.txt/);
  assert.equal(editor.selectionStart, 2);
  assert.equal(h.document.getElementById('micButtonText').textContent, 'Voice input');
  assert.equal(h.document.getElementById('rtEnterButton').getAttribute('aria-label'), dictionaries.en['realtime.hold.to.send.enter.at.least.0.6.seconds']);
  assert.ok(h.requests.some(([url]) => url === '/api/state'));
  h.responses.set('/api/state', { status: 200, data: { role: 'phone', status: 'ready', text: '', revision: 0, generation: 'test' } });
  h.document.getElementById('retryButton').dispatchEvent({ type: 'click' });
  await h.settle();
  h.document.getElementById('micButton').dispatchEvent({ type: 'click' });
  h.window.I18n.setLanguage('ja');
  assert.equal(h.document.getElementById('desktopPanel').hidden, true);
  assert.equal(h.document.getElementById('voiceHint').hidden, false);
  assert.match(h.document.getElementById('voiceHintText').textContent, /システムの音声入力/);
  h.responses.set('/api/realtime/request', { status: 422, data: { code: 'invalid_nonce', message: 'server fallback' } });
  await h.window.FastInputRealtime.requestStart();
  assert.equal(h.document.getElementById('rtAlertText').textContent, dictionaries.ja['error.invalid_nonce']);
  h.document.getElementById('rtInput').value = 'Locked user text';
  await h.window.FastInputRealtime.requestStart();
  assert.match(h.document.getElementById('rtLockedList').textContent, /リアルタイム入力を終了しました/);
  h.window.I18n.setLanguage('en');
  assert.equal(h.document.getElementById('rtAlertText').textContent, dictionaries.en['error.invalid_nonce']);
  assert.match(h.document.getElementById('rtLockedList').textContent, /Real-time typing ended/);
  assert.match(h.document.getElementById('rtLockedList').textContent, /Locked user text/);
});
console.log('WEB keys per language=' + Object.keys(dictionaries.en).length);
console.log('TOTAL passed=' + passed + ' failed=' + failed);
if (failed) process.exitCode = 1;
