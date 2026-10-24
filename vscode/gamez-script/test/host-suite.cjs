const vscode = require('vscode');
const assert = require('node:assert/strict');
exports.run = async function() {
  const file = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/zrdr/anim.zad');
  const doc = await vscode.workspace.openTextDocument(file); await vscode.window.showTextDocument(doc);
  assert.equal(doc.languageId,'gamez-script');
  const extension = vscode.extensions.getExtension('Vorta.gamez-script'); assert(extension); await extension.activate();
  const validation = await vscode.commands.executeCommand('gamez.server.validateProject',file.toString());
  assert.equal(validation.files,3,'Project validation with native VS Code URI');
  await Promise.all([vscode.commands.executeCommand('gamez.restartLanguageServer'), vscode.commands.executeCommand('gamez.restartLanguageServer')]);
  const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider',file);
  assert(symbols.length>0,'Document outline');
  const hover = await vscode.commands.executeCommand('vscode.executeHoverProvider',file,new vscode.Position(0,2));
  assert(hover.length>0,'Hover');
  const editor = vscode.window.activeTextEditor;
  const partial = '(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (ACTIVATION ( ';
  await editor.edit(edit=>edit.replace(new vscode.Range(doc.positionAt(0),doc.positionAt(doc.getText().length)),partial));
  const completion = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider',file,doc.positionAt(partial.length));
  assert(completion.items.some(i=>(typeof i.label==='string'?i.label:i.label.label)==='ON_CALL'),'Contextual completion');
  const onCall = completion.items.find(i=>(typeof i.label==='string'?i.label:i.label.label)==='ON_CALL');
  assert(onCall.documentation.value.includes('WEAPON_HIT'),'Markdown completion documentation');
  await editor.edit(edit=>edit.insert(doc.positionAt(doc.getText().length),'é'));
  await new Promise(resolve=>setTimeout(resolve,800));
  assert(vscode.languages.getDiagnostics(file).some(d=>d.code==='GAMEZ_FILE_ENCODING'),'Encoding mismatch');
  await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  for (const name of ['bom.zrd','utf16.zrd']) {
    const uri = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/zrdr',name);
    await vscode.workspace.openTextDocument(uri);
    await new Promise(resolve=>setTimeout(resolve,400));
    assert(vscode.languages.getDiagnostics(uri).some(d=>d.code==='GAMEZ_FILE_ENCODING'),name+' encoding mismatch');
  }
  console.log('PASS GameZ Script real VS Code extension-host language, outline, hover, completion and encoding diagnostics');
};
