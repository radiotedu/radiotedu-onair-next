#!/usr/bin/env python3
"""Pinned native macOS shared codec runtime. Run only on disposable CI."""
import importlib.util, json, os, pathlib, platform, re, shutil, subprocess
HERE=pathlib.Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('pinned',HERE/'build-windows-codecs.py')
pinned=importlib.util.module_from_spec(spec);spec.loader.exec_module(pinned)
OUT=pinned.REPO/'artifacts/macos-codec-runtime'
WORK=pathlib.Path(os.environ.get('RUNNER_TEMP','/tmp'))/'onair-macos-codec-build'

def main():
    if os.environ.get('GITHUB_ACTIONS')!='true' or platform.system()!='Darwin':
        raise RuntimeError('Use a disposable macOS CI runner.')
    if OUT.exists() or WORK.exists(): raise RuntimeError('Choose a fresh build directory.')
    for directory in [OUT/'bin',OUT/'Sources',OUT/'Licenses',WORK]: directory.mkdir(parents=True)
    pinned.OUT=OUT;pinned.WORK=WORK
    fdk=pinned.source('fdk');ffmpeg=pinned.source('ffmpeg');prefix=WORK/'prefix'
    env=dict(os.environ,CC='clang',CXX='clang++',CFLAGS='-O2',CXXFLAGS='-O2',MACOSX_DEPLOYMENT_TARGET='12.0')
    pinned.run(['./autogen.sh'],fdk,env)
    fdk_args=['./configure','--prefix='+str(prefix),'--enable-shared','--disable-static']
    pinned.run(fdk_args,fdk,env);pinned.run(['make','-j3'],fdk,env);pinned.run(['make','install'],fdk,env)
    env['PKG_CONFIG_LIBDIR']=str(prefix/'lib/pkgconfig');env['PKG_CONFIG_PATH']=''
    ff_args=['./configure','--prefix='+str(prefix),'--cc=clang','--cxx=clang++','--enable-shared','--disable-static','--disable-gpl','--disable-version3','--disable-nonfree','--disable-autodetect','--enable-libfdk-aac','--enable-pthreads','--disable-avdevice','--disable-doc','--disable-debug','--disable-ffplay','--enable-securetransport','--extra-cflags=-O2']
    pinned.run(ff_args,ffmpeg,env)
    configuration=(ffmpeg/'ffbuild/config.mak').read_text()
    if not re.search(r'^!CONFIG_GPL=yes$',configuration,re.M) or not re.search(r'^!CONFIG_NONFREE=yes$',configuration,re.M):raise RuntimeError('GPL/nonfree must remain disabled.')
    pinned.run(['make','-j3'],ffmpeg,env);pinned.run(['make','install'],ffmpeg,env)
    for name in ['ffmpeg','ffprobe']:shutil.copy2(prefix/'bin'/name,OUT/'bin'/name)
    for library in (prefix/'lib').glob('*.dylib'):shutil.copy2(library,OUT/'bin'/library.name)
    for binary in (OUT/'bin').iterdir():
        if binary.suffix=='.dylib':pinned.run(['install_name_tool','-id','@rpath/'+binary.name,binary])
        dependencies=subprocess.check_output(['otool','-L',binary],text=True).splitlines()[1:]
        for line in dependencies:
            dependency=line.strip().split(' (')[0]
            if dependency.startswith(str(prefix)):
                replacement='@loader_path/'+pathlib.Path(dependency).name
                if not (OUT/'bin'/pathlib.Path(dependency).name).exists():raise RuntimeError('Unbundled codec dependency.')
                pinned.run(['install_name_tool','-change',dependency,replacement,binary])
            elif not dependency.startswith(('/usr/lib/','/System/Library/','@rpath/','@loader_path/')):
                raise RuntimeError('Unexpected dependency: '+dependency)
        pinned.run(['codesign','--force','--sign','-',binary])
    shutil.copy2(fdk/'NOTICE',OUT/'Licenses/FDK-AAC-NOTICE.txt')
    for name in ['COPYING.LGPLv2.1','LICENSE.md']:shutil.copy2(ffmpeg/name,OUT/'Licenses'/name)
    for source in ['build-macos-codecs.py','build-windows-codecs.py','windows-codec.lock.json']:shutil.copy2(HERE/source,OUT/'Sources'/source)
    (OUT/'Sources/build-configurations.json').write_text(json.dumps({'fdk':fdk_args,'ffmpeg':ff_args,'architecture':platform.machine()},indent=2))
    manifest={'schemaVersion':1,'target':'osx-arm64' if platform.machine()=='arm64' else 'osx-x64','upstreams':pinned.LOCK,'files':[]}
    for item in sorted(OUT.rglob('*')):
        if item.is_file():manifest['files'].append({'path':item.relative_to(OUT).as_posix(),'bytes':item.stat().st_size,'sha256':pinned.sha(item)})
    (OUT/'runtime-manifest.json').write_text(json.dumps(manifest,indent=2))
    pinned.run([OUT/'bin/ffmpeg','-hide_banner','-encoders'])
if __name__=='__main__':main()
