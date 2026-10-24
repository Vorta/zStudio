const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { Registry, parseRawGrammar } = require('vscode-textmate');
const onig = require('vscode-oniguruma');
(async () => {
  const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
  await onig.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
  const grammarFile = path.resolve(__dirname, '../syntaxes/gamez.tmLanguage.json');
  const registry = new Registry({ onigLib: Promise.resolve({ createOnigScanner: s => new onig.OnigScanner(s), createOnigString: s => new onig.OnigString(s) }), loadGrammar: async () => parseRawGrammar(fs.readFileSync(grammarFile, 'utf8'), grammarFile) });
  const grammar = await registry.loadGrammar('source.gamez');
  const scopes = grammar.tokenizeLine('CALL_ANIMATION ( NAME ( child ) START_TIME ( EVENT_OFFSET 10.0 ) ) # hello').tokens.flatMap(t => t.scopes);
  for (const scope of ['keyword.control.gamez', 'constant.numeric.float.gamez', 'comment.line.number-sign.gamez', 'support.type.property-name.gamez']) assert(scopes.includes(scope), scope);
  const stringScopes = grammar.tokenizeLine('"path\\\\file\\x20.zan" f32:7FC00001').tokens.flatMap(t => t.scopes);
  assert(stringScopes.includes('constant.character.escape.gamez')); assert(stringScopes.includes('constant.numeric.float.raw.gamez'));
  registry.dispose(); console.log('PASS GameZ Script grammar');
})().catch(e => { console.error(e); process.exitCode = 1; });
