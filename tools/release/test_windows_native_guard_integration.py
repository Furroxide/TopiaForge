"""Verify native-path guard child scope and the installed C++ target graph."""
import argparse
import re
import time
from pathlib import Path
import html
import json
import os
import subprocess
import sys


def run(source, evidence, redact, args):
    fixture = args.temporary_root.resolve() / ('attempt-'+str(time.time_ns()))
    fixture.mkdir(parents=True, exist_ok=False)
    msbuild = args.msbuild.resolve()
    vc = args.vc_targets.resolve()
    pwsh = str(args.pwsh.resolve())
    if not Path(pwsh).is_file() or not msbuild.is_file() or not (vc/'Microsoft.Cpp.targets').is_file():
        raise RuntimeError('Existing compiler-graph prerequisites unavailable')

    def call(name, command):
        result = subprocess.run(command, cwd=fixture, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            encoding='utf-8', errors='replace', timeout=120, check=False,
            creationflags=subprocess.CREATE_NO_WINDOW)
        text = redact(result.stdout)
        text = text.replace(str(fixture), '<fixture>')
        (evidence/(name+'.log')).write_text(text, encoding='utf-8')
        if result.returncode:
            raise RuntimeError(name + ': failed; retain phase log')

    (fixture/'sample.cpp').write_text('int native_guard_fixture = 1;\n', encoding='utf-8')
    units = lambda value: len(str(value).encode('utf-16-le')) // 2
    long_object = fixture/'long-output'
    while units(long_object) + 30 < 260:
        long_object /= 'segment012345'
    long_object /= 'x'*(260-units(long_object)-1-len('.obj'))+'.obj'
    if units(long_object) != 260 or not long_object.is_relative_to(fixture):
        raise RuntimeError('Bounded long-object fixture path invalid')

    def project(name, obj, excluded=False):
        esc = lambda value:html.escape(str(value), quote=True)
        (fixture/name).write_text(f'''<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><ProjectGuid>{{48DBDE82-0A20-4A6A-8E49-31E8BB447FE0}}</ProjectGuid><VCTargetsPath>{esc(vc)}\\</VCTargetsPath></PropertyGroup>
  <Import Project="$(VCTargetsPath)Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>StaticLibrary</ConfigurationType><PlatformToolset>v143</PlatformToolset><UseDebugLibraries>false</UseDebugLibraries></PropertyGroup>
  <Import Project="$(VCTargetsPath)Microsoft.Cpp.props" />
  <PropertyGroup><IntDir>{esc(fixture/'obj')}\\</IntDir><OutDir>{esc(fixture/'out')}\\</OutDir><TargetName>sample</TargetName></PropertyGroup>
  <ItemDefinitionGroup><ClCompile><ObjectFileName>{esc(obj)}</ObjectFileName></ClCompile></ItemDefinitionGroup>
  <ItemGroup><ClCompile Include="sample.cpp"><ExcludedFromBuild>{str(excluded).lower()}</ExcludedFromBuild></ClCompile></ItemGroup>
  <Import Project="$(VCTargetsPath)Microsoft.Cpp.targets" />
</Project>''', encoding='utf-8')

    project('sample.vcxproj', str(fixture/'obj')+'\\')
    project('long.vcxproj', long_object)
    project('excluded.vcxproj', str(fixture/'obj')+'\\', excluded=True)
    call('actual-cpp-and-environment', [pwsh,'-NoProfile','-File',
        str(Path(__file__).with_name('test_windows_native_guard_integration.ps1')),
        '-Source',str(source),'-Output',str(fixture),'-Msbuild',str(msbuild)])
    call('marker-regressions', [sys.executable, '-I',
        str(source/'tools/release/test_windows_native_path_guard.py'),
        '--msbuild', str(msbuild), '--temporary-root', str(fixture/'markers'),
        '--receipt-dir', str(evidence/'markers')])
    result = json.loads((fixture/'integration-result.json').read_text(encoding='utf-8-sig'))
    if len(result['cases']) != 7 or not all(c['passed'] for c in result['cases']):
        raise AssertionError('Incomplete actual integration result')
    long_log = (evidence/'actual-cpp-and-environment.log').read_text(encoding='utf-8')
    if 'TFPATH001' not in long_log:
        raise AssertionError('Long output failed for an unrelated reason')
    (evidence/'integration-result.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--msbuild', type=Path, required=True)
    parser.add_argument('--vc-targets', type=Path, required=True)
    parser.add_argument('--pwsh', type=Path, required=True)
    parser.add_argument('--temporary-root', type=Path, required=True)
    parser.add_argument('--receipt-dir', type=Path, required=True)
    args = parser.parse_args()
    if os.name != 'nt' or os.environ.get('HEAVY_LOCK_TEST_DIR'):
        raise RuntimeError('Installed Windows tools and the canonical lock are required')
    evidence = args.receipt_dir.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parents[2]
    def redact(text):
        for path, label in ((str(source), '<repository>'), (str(args.temporary_root.resolve()), '<fixture-root>'),
                            (os.environ.get('USERPROFILE', ''), '<account-root>')):
            if path:
                for form in (path, path.replace('\\', '/')):
                    text = re.sub(re.escape(form), lambda _: label, text, flags=re.IGNORECASE)
        return text
    return run(source, evidence, redact, args)


if __name__ == '__main__':
    raise SystemExit(main())
