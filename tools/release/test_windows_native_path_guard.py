"""Exercise the imported guard with MSBuild target stubs; never starts a compiler."""
from pathlib import Path
import argparse
import hashlib
import html
import json
import os
import re
import subprocess
import time

HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parent.parent
GUARD = HERE / 'windows-native-path-guard.targets'



def windows_path(units):
    value = 'C:\\guard\\'
    while len(value) + 30 < units:
        value += 'segment0123456789\\'
    return value + 'x' * (units - len(value))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--msbuild', type=Path, required=True,
                        help='An already installed, pinned Windows MSBuild executable.')
    parser.add_argument('--temporary-root', type=Path, required=True,
                        help='A short private directory for fresh marker fixtures.')
    parser.add_argument('--receipt-dir', type=Path, required=True,
                        help='A new private directory; existing evidence is never overwritten.')
    args = parser.parse_args()
    MSBUILD = args.msbuild.resolve()
    TEMP = args.temporary_root.resolve()
    if os.name != 'nt':
        raise RuntimeError('This fixture requires installed Windows MSBuild.')
    if not MSBUILD.is_file() or os.environ.get('HEAVY_LOCK_TEST_DIR'):
        raise RuntimeError('Installed tool/canonical lock precondition failed')
    TEMP.mkdir(parents=True, exist_ok=True)
    receipt = args.receipt_dir.resolve()
    receipt.mkdir(parents=True, exist_ok=False)
    campaign = TEMP / ('attempt-' + str(time.time_ns()))
    campaign.mkdir()
    before = hashlib.sha256(GUARD.read_bytes()).hexdigest()
    rows = []
    cases = [
        ('object-259', dict(obj=windows_path(259)), True),
        ('object-260', dict(obj=windows_path(260)), False),
        ('object-264', dict(obj=windows_path(264)), False),
        ('intermediate-260', dict(intermediate=windows_path(260)), False),
        ('tracking-file-260', dict(tracking=windows_path(260 - len('CL.command.1.tlog') - 1) + '\\'), False),
        ('pdb-260', dict(pdb=windows_path(260)), False),
        ('unknown-objects', dict(obj=''), False),
        ('missing-tracking', dict(tracking=''), False),
        ('utf16-object-260', dict(obj=windows_path(258) + '\U0001f916'), False),
    ]
    deadline = time.monotonic() + 100

    def run(name, changes, expected, guarded=True):
        values = dict(intermediate='C:\\guard\\obj\\', tracking='C:\\guard\\tlog\\',
                      obj='C:\\guard\\obj\\sample.obj', pdb='')
        values.update(changes)
        output = campaign / name
        output.mkdir()
        marker = output / 'compiler-started.txt'
        project = output / 'fixture.proj'
        esc = lambda value: html.escape(str(value), quote=True)
        import_line = f'<Import Project="{esc(GUARD)}" />' if guarded else ''
        object_line = f'<Obj Include="{esc(values["obj"])}" />' if values['obj'] else ''
        project.write_text(f'''<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><IntDir>{esc(values['intermediate'])}</IntDir><TLogLocation>{esc(values['tracking'])}</TLogLocation></PropertyGroup>
  <ItemGroup><ClCompile Include="sample.cpp"><ProgramDataBaseFileName>{esc(values['pdb'])}</ProgramDataBaseFileName></ClCompile>{object_line}</ItemGroup>
  {import_line}
  <Target Name="Build" DependsOnTargets="PrepareForBuild;ClCompile" />
  <Target Name="PrepareForBuild" />
  <Target Name="ComputeCLOutputs" />
  <Target Name="ClCompile"><WriteLinesToFile File="{esc(marker)}" Lines="stub only" Overwrite="true" /></Target>
</Project>''', encoding='utf-8')
        limit = min(15, deadline - time.monotonic())
        if limit <= 0:
            raise TimeoutError('Guard proof deadline exceeded')
        result = subprocess.run([str(MSBUILD), str(project), '/nologo', '/m:1', '/nr:false', '/v:minimal'],
                                cwd=output, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                timeout=limit, text=True, encoding='utf-8', errors='replace',
                                creationflags=subprocess.CREATE_NO_WINDOW, check=False)
        text = result.stdout
        for path, label in ((str(campaign), '<fixture>'), (str(REPOSITORY), '<repository>'),
                            (os.environ.get('USERPROFILE', ''), '<account-root>')):
            if path:
                for form in (path, path.replace('\\', '/')):
                    text = re.sub(re.escape(form), lambda _: label, text, flags=re.IGNORECASE)
        (receipt / (name + '.log')).write_text(text, encoding='utf-8')
        started = marker.is_file()
        actual = result.returncode == 0 and started
        rows.append(dict(name=name, exit=result.returncode, compilerStubReached=started,
                         expectedSuccess=expected, guarded=guarded))
        if actual != expected or (not expected and (started or 'TFPATH00' not in text)):
            raise AssertionError(name + ': guard/stub boundary failed')

    # Controls prove the paths alone do not stop the marker target. The imported
    # guard must prevent that same target from being reached, before any compiler.
    for name, changes, expected in cases:
        if not expected:
            run('unguarded-' + name, changes, True, guarded=False)
        run(name, changes, expected)
    if hashlib.sha256(GUARD.read_bytes()).hexdigest() != before:
        raise RuntimeError('Guard source changed during proof')
    (receipt / 'result.json').write_text(json.dumps(dict(guardSha256=before, cases=rows), indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), guarded=len(cases), failed=0, nativeCompilerStarted=False)))


if __name__ == '__main__':
    main()
