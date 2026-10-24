const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const {runTests} = require('@vscode/test-electron');
(async()=>{
  const profile = fs.mkdtempSync(path.join(os.tmpdir(),'gamez-vscode-'));
  const workspace = path.join(profile,'project');
  fs.mkdirSync(path.join(workspace,'gamegen'),{recursive:true}); fs.mkdirSync(path.join(workspace,'data/m1/zrdr'),{recursive:true});
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/anim.zad'),'(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (child)))))');
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/bom.zrd'),'\ufeff(TEST (ascii))','utf8');
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/utf16.zrd'),'\ufeff(TEST (ascii))','utf16le');
  try {
    await runTests({ vscodeExecutablePath: process.env.GAMEZ_VSCODE, version:'stable', extensionDevelopmentPath:path.resolve(__dirname,'..'), extensionTestsPath:path.join(__dirname,'host-suite.cjs'),
      launchArgs:[workspace,'--user-data-dir',path.join(profile,'profile'),'--extensions-dir',path.join(profile,'extensions'),'--disable-workspace-trust','--skip-welcome','--skip-release-notes','--disable-updates','--disable-telemetry'] });
  } finally { fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:300}); }
})().catch(e=>{console.error(e);process.exitCode=1;});
