#!/usr/bin/env python3
"""Create a self-contained fresh-install macOS PKG on disposable CI only."""
import hashlib,json,os,pathlib,plistlib,platform,shutil,subprocess,sys
ROOT=pathlib.Path(__file__).resolve().parents[1]
def run(args,**kwargs):subprocess.run([str(x) for x in args],check=True,**kwargs)
if os.environ.get('GITHUB_ACTIONS')!='true' or platform.system()!='Darwin':raise RuntimeError('Disposable macOS CI only.')
rid=sys.argv[1]
if rid!=('osx-arm64' if platform.machine()=='arm64' else 'osx-x64'):raise RuntimeError('Build on the matching native architecture.')
stage=ROOT/'artifacts/macos-package';app=stage/'Applications/RadioTEDU OnAir Next.app'
if stage.exists():raise RuntimeError('Choose a fresh staging directory.')
run(['npm','ci','--ignore-scripts','--no-audit','--no-fund'],cwd=ROOT/'desktop-shell')
run(['node','node_modules/electron/install.js'],cwd=ROOT/'desktop-shell')
electron=ROOT/'desktop-shell/node_modules/electron/dist/Electron.app'
run([electron/'Contents/MacOS/Electron',ROOT/'desktop-shell/build-icons.cjs'])
shutil.copytree(electron,app,symlinks=True)
resources=app/'Contents/Resources'
for project,folder in [('OnAirNext.Host','engine'),('OnAirNext.Web','console')]:
    lock=ROOT/'src'/project/'packages.lock.json';before=json.loads(lock.read_text())
    run(['dotnet','publish',ROOT/'src'/project/(project+'.csproj'),'-c','Release','-r',rid,'--self-contained','true','-o',resources/folder,'-m:1','/p:UseSharedCompilation=false','/p:RestoreLockedMode=false','/p:DebugType=None'])
    after=json.loads(lock.read_text())
    for framework,packages in before['dependencies'].items():
        for name,entry in packages.items():
            if entry.get('resolved') and framework in after['dependencies']:
                actual=after['dependencies'][framework].get(name,{})
                if entry.get('resolved')!=actual.get('resolved') or entry.get('contentHash')!=actual.get('contentHash'):raise RuntimeError('Runtime expansion changed '+name)
    lock.write_text(json.dumps(before,indent=2))
shutil.copytree(ROOT/'artifacts/macos-codec-runtime',resources/'audio-runtime')
appcode=resources/'app';appcode.mkdir()
for name in ['main.cjs','policy.cjs','shortcut.cjs','package.json','icon.png','icon.ico']:shutil.copy2(ROOT/'desktop-shell'/name,appcode/name)
info_path=app/'Contents/Info.plist';info=plistlib.loads(info_path.read_bytes())
info.update(CFBundleIdentifier='com.radiotedu.onairnext',CFBundleName='RadioTEDU OnAir Next',CFBundleDisplayName='RadioTEDU OnAir Next',CFBundleShortVersionString='1.1.0',CFBundleVersion='1.1.0',NSMicrophoneUsageDescription='Send the selected microphone to your chosen radio channel after you press Go live.',LSMinimumSystemVersion='14.0')
info_path.write_bytes(plistlib.dumps(info))
run(['iconutil','-c','icns',ROOT/'desktop-shell/OnAirNext.iconset','-o',resources/'electron.icns'])
# Native .NET apphosts and codec libraries remain independently executable; ad-hoc
# signing is for qualification, not a claim of Apple Developer ID notarization.
run(['codesign','--force','--deep','--sign','-',app])
run(['codesign','--verify','--deep','--strict',app])
# Record the final packaged native signatures as well as the pinned source inputs.
runtime_manifest=resources/'audio-runtime/runtime-manifest.json'
manifest=json.loads(runtime_manifest.read_text())
for entry in manifest['files']:
    content=(resources/'audio-runtime'/entry['path']).read_bytes();entry['bytes']=len(content);entry['sha256']=hashlib.sha256(content).hexdigest()
runtime_manifest.write_text(json.dumps(manifest,indent=2))
run(['codesign','--force','--sign','-',app])
run(['codesign','--verify','--deep','--strict',app])
for script in (ROOT/'packaging/macos/scripts').iterdir():script.chmod(0o755)
out=ROOT/'artifacts/installers';out.mkdir(exist_ok=True)
component=out/('OnAirNext-'+rid+'-component.pkg')
run(['pkgbuild','--root',stage,'--identifier','com.radiotedu.onairnext','--version','1.1.0','--install-location','/','--scripts',ROOT/'packaging/macos/scripts',component])
welcome=out/'welcome.txt';welcome.write_text('RadioTEDU OnAir Next\nDeveloper: RadioTEDU Technologies / RTAI\n\nInstalls the native broadcast engine, desktop window and complete audio runtime. Requires macOS 14 or newer. Sign in as the studio operator before installing. A fresh setup has no armed outputs. Existing installations are preserved; this package refuses to overwrite one.\n\nThis preview package is ad-hoc signed, not Apple Developer ID signed or notarized.\n')
distribution=out/'distribution.xml';distribution.write_text('<?xml version="1.0"?><installer-gui-script minSpecVersion="2"><title>RadioTEDU OnAir Next</title><organization>com.radiotedu</organization><welcome file="welcome.txt"/><options customize="never" require-scripts="true"/><domains enable_localSystem="true" enable_currentUserHome="false" enable_anywhere="false"/><choices-outline><line choice="default"/></choices-outline><choice id="default" visible="false"><pkg-ref id="com.radiotedu.onairnext"/></choice><pkg-ref id="com.radiotedu.onairnext" version="1.1.0">'+component.name+'</pkg-ref></installer-gui-script>')
run(['productbuild','--distribution',distribution,'--resources',out,'--package-path',out,out/('RadioTEDU-OnAir-Next-'+rid+'-Setup.pkg')])
print('Created native '+rid+' setup; installation and audio qualification must pass before publishing.')
