$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'Package on a disposable Windows CI runner only.'}
$root=Split-Path $PSScriptRoot -Parent
$package=Join-Path $root 'artifacts/windows-package'
$desktop=Join-Path $root 'desktop-shell'
Push-Location $desktop
try{
 & npm ci --ignore-scripts --no-audit --no-fund
 if($LASTEXITCODE -ne 0){throw 'Desktop dependency restore failed.'}
 & node node_modules/electron/install.js
 if($LASTEXITCODE -ne 0){throw 'Pinned Electron download failed.'}
 & npm test
 if($LASTEXITCODE -ne 0){throw 'Desktop policy tests failed.'}
 & ./node_modules/electron/dist/electron.exe ./build-icons.cjs
 if($LASTEXITCODE -ne 0){throw 'Application icon render failed.'}
 & ./node_modules/electron/dist/electron.exe ./test/smoke-main.cjs
 if($LASTEXITCODE -ne 0){throw 'Desktop window qualification failed.'}
 & ./node_modules/electron/dist/electron.exe ./test/operator-e2e.cjs
 if($LASTEXITCODE -ne 0){throw 'Operator UI qualification failed.'}
}finally{Pop-Location}
$stage=Join-Path $package 'desktop'
if(Test-Path -LiteralPath $stage){throw 'Desktop staging already exists.'}
Copy-Item -LiteralPath (Join-Path $desktop 'node_modules/electron/dist') -Destination $stage -Recurse
Move-Item -LiteralPath (Join-Path $stage 'electron.exe') -Destination (Join-Path $stage 'RadioTEDU OnAir Next.exe')
$appDir=Join-Path $stage 'resources/app'
$null=New-Item -ItemType Directory -Path $appDir
foreach($name in @('main.cjs','policy.cjs','shortcut.cjs','package.json','icon.png','icon.ico')){Copy-Item -LiteralPath (Join-Path $desktop $name) -Destination $appDir}
Copy-Item -LiteralPath (Join-Path $root 'artifacts/windows-codec-runtime') -Destination (Join-Path $package 'audio-runtime') -Recurse
$compiler=Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if(-not(Test-Path -LiteralPath $compiler)){throw 'The CI image must provide Inno Setup 6.'}
& $compiler ('/DPackageRoot='+$package) (Join-Path $root 'packaging/windows/setup.iss')
if($LASTEXITCODE -ne 0){throw 'Windows installer compilation failed.'}
$installer=Join-Path $root 'artifacts/installers/RadioTEDU-OnAir-Next-Windows-x64-Setup.exe'
if(-not(Test-Path -LiteralPath $installer)){throw 'Windows installer artifact was not created.'}
@{Source=$env:GITHUB_SHA;Setup=(Get-Item -LiteralPath $installer).Name;Bytes=(Get-Item -LiteralPath $installer).Length;Sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;Signed=$false}|ConvertTo-Json|Set-Content -LiteralPath ($installer+'.json') -Encoding utf8NoBOM
