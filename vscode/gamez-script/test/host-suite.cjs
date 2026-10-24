const vscode = require('vscode');
const assert = require('node:assert/strict');
exports.run = async function() {
  const file = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/zrdr/anim.zad');
  const doc = await vscode.workspace.openTextDocument(file); await vscode.window.showTextDocument(doc);
  assert.equal(doc.languageId,'gamez-script');
  const extension = vscode.extensions.getExtension('Vorta.gamez-script'); assert(extension); await extension.activate();
  await new Promise(resolve=>setTimeout(resolve,600)); // Let the initial didOpen background pass settle.
  const validation = await vscode.commands.executeCommand('gamez.server.validateProject',file.toString());
  assert.equal(validation.files,3,'Project validation with native VS Code URI');
  // This minimal project has no world script, giving a deep-only finding to invalidate.
  const deepErrors = ()=>vscode.languages.getDiagnostics(file).filter(d=>d.code==='RECOIL_PROJECT' && d.message.includes('World binding validation'));
  assert(deepErrors().length>0,'Deep validation finding is published');
  const recipe = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/coast.terrain.json');
  await vscode.workspace.fs.writeFile(recipe, Buffer.from('{}'));
  for(let attempt=0;attempt<50 && deepErrors().length>0;attempt++) await new Promise(resolve=>setTimeout(resolve,100));
  assert.equal(deepErrors().length,0,'Saved terrain recipe invalidates deep validation');
  await new Promise(resolve=>setTimeout(resolve,400));
  assert.equal(deepErrors().length,0,'Invalidation stays clear after local and project publications');
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
  // Accept actual suggestions: provider labels alone do not prove safe replacement.
  const numeric = '(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (CALLBACK (VALUE (91)))))))';
  const quoted = '(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME ("rocket_port1*")) ANIMATION_DEFINITION (NAME (caller) SEQUENCE_DEFINITION (CALL_ANIMATION (NAME ("rocket_p")))))))';
  const escaped = quoted.replace('NAME ("rocket_p")', 'NAME ("rocket\\x5Fp")');
  const quotedEnum = '(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ACTIVATION ("ON_")))))';
  for (const [text, marker, expected] of [[numeric, '91', 'VALUE (911)'], [quoted, 'rocket_p"', 'CALL_ANIMATION (NAME ("rocket_port1*"))'], [escaped, 'rocket\\x5Fp"', 'CALL_ANIMATION (NAME ("rocket_port1*"))'], [quotedEnum, 'ON_"', 'ACTIVATION (ON_CALL)']]) {
    await editor.edit(edit=>edit.replace(new vscode.Range(doc.positionAt(0),doc.positionAt(doc.getText().length)),text));
    const at = text.lastIndexOf(marker) + (marker.endsWith('"') ? marker.length-1 : marker.length);
    editor.selection = new vscode.Selection(doc.positionAt(at), doc.positionAt(at));
    const items = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', file, doc.positionAt(at));
    assert(items.items.some(item=>item.range), 'Completion has an explicit replacement range');
    await vscode.commands.executeCommand('editor.action.triggerSuggest');
    await new Promise(resolve=>setTimeout(resolve,800));
    await vscode.commands.executeCommand('acceptSelectedSuggestion');
    assert(doc.getText().includes(expected), 'Accepted completion replaces the complete numeric/quoted source token: '+doc.getText());
  }
  await editor.edit(edit=>edit.insert(doc.positionAt(doc.getText().length),'é'));
  await new Promise(resolve=>setTimeout(resolve,800));
  assert(vscode.languages.getDiagnostics(file).some(d=>d.code==='GAMEZ_FILE_ENCODING'),'Encoding mismatch');
  await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  const vehicleUri = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/zrdr/vehicle.zrd');
  await vscode.workspace.fs.writeFile(vehicleUri,Buffer.from('(stealth (common_mode (position (1 2 3) camera_ud_swing (5.5 2.5 1.2 1.2))))'));
  const vehicle = await vscode.workspace.openTextDocument(vehicleUri);
  const vehicleEditor = await vscode.window.showTextDocument(vehicle);
  const at = vehicle.getText().indexOf('5.5');
  await vehicleEditor.edit(edit=>edit.replace(new vscode.Range(vehicle.positionAt(at), vehicle.positionAt(at+3)), '5'));
  const typeErrors = ()=>vscode.languages.getDiagnostics(vehicle.uri).filter(d=>d.code==='RECOIL_TYPE');
  for(let attempt=0;attempt<50 && typeErrors().length===0;attempt++) await new Promise(resolve=>setTimeout(resolve,100));
  assert.equal(typeErrors().length,1,'Unsaved float-to-integer edit produces one error');
  assert.equal(typeErrors()[0].severity,vscode.DiagnosticSeverity.Error,'Float type mismatch is red/error severity');
  assert.equal(vehicle.getText(typeErrors()[0].range),'5','Underline covers only the changed integer');
  const unused = vscode.languages.getDiagnostics(vehicle.uri).find(d=>d.code==='RECOIL_IGNORED');
  assert.equal(unused.message,"position is not used in common_mode. Set the vehicle's position in aiv.zrd.");
  assert.equal(unused.severity,vscode.DiagnosticSeverity.Warning);
  await vehicleEditor.edit(edit=>edit.replace(typeErrors()[0].range,'5.0'));
  for(let attempt=0;attempt<50 && typeErrors().length>0;attempt++) await new Promise(resolve=>setTimeout(resolve,100));
  assert.equal(typeErrors().length,0,'Restoring a float clears the error');
  await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  for (const name of ['bom.zrd','utf16.zrd']) {
    const uri = vscode.Uri.joinPath(vscode.workspace.workspaceFolders[0].uri,'data/m1/zrdr',name);
    await vscode.workspace.openTextDocument(uri);
    await new Promise(resolve=>setTimeout(resolve,400));
    assert(vscode.languages.getDiagnostics(uri).some(d=>d.code==='GAMEZ_FILE_ENCODING'),name+' encoding mismatch');
  }
  console.log('PASS GameZ Script real VS Code extension-host language, outline, hover, completion and encoding diagnostics');
};
