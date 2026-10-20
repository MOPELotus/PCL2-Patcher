#!/usr/bin/env python3
"""Independently verify all five restored methods, then compare whole-EXE ILVerify diagnostics."""
import argparse, json, os, pathlib, re, subprocess
ROOT = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('original', type=pathlib.Path)
p.add_argument('patched', type=pathlib.Path)
p.add_argument('--output', type=pathlib.Path, default=ROOT / '.artifacts/ilverify')
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
dotnet = os.environ.get('DOTNET', 'dotnet')
def run(arguments, check=True):
    result = subprocess.run(arguments, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if check and result.returncode:
        raise RuntimeError(result.stdout)
    return result
run([dotnet, 'tool', 'restore'])
refs = a.output / 'references'
run([dotnet, 'run', '--project', 'tools/PclPatcher.Audit', '-c', 'Release', '--', str(a.patched.resolve()), str(refs.resolve())])
nuget = run([dotnet, 'nuget', 'locals', 'global-packages', '--list']).stdout.split(': ', 1)[1].strip()
framework = pathlib.Path(nuget) / 'microsoft.netframework.referenceassemblies.net48/1.0.3/build/.NETFramework/v4.8'
reference_args = []
for reference in sorted([*framework.glob('*.dll'), *framework.glob('Facades/*.dll'), *refs.glob('*.dll')]):
    reference_args += ['-r', str(reference.resolve())]
# Hundreds of long NuGet paths exceed Windows CreateProcess's 32K command-line limit.
# ILVerify's System.CommandLine parser supports response files; keep paths quoted and
# use forward slashes so backslashes cannot be interpreted as response-file escapes.
response = a.output / 'references.rsp'
response.write_text('\n'.join('"' + value.replace('\\', '/').replace('"', '\\"') + '"' for value in reference_args) + '\n', encoding='utf-8')
reference_args = ['@' + str(response.resolve())]
if not (framework / 'mscorlib.dll').exists():
    raise RuntimeError('Missing .NET Framework 4.8 reference assemblies')
pattern = '(PageDownloadInstall.*LoaderInit|ModModpack.*ModpackInstall|PageDownloadResourceDetail.*InitializeStackResourceVersions|PageDownloadModify.*LoadInstanceFinished)'
focused = run([dotnet, 'ilverify', str(a.patched.resolve()), '--statistics', '-i', pattern, *reference_args], check=False)
(a.output / 'patched-methods.txt').write_text(focused.stdout, encoding='utf-8')
if focused.returncode != 0 or not re.search(r'Methods verified:\s*5\b', focused.stdout):
    raise RuntimeError('Expected all five restored methods to pass independent ILVerify:\n' + focused.stdout)
all_results = {}
for label, path in [('original', a.original), ('patched', a.patched)]:
    result = run([dotnet, 'ilverify', str(path.resolve()), '--statistics', *reference_args], check=False)
    (a.output / f'full-{label}.txt').write_text(result.stdout, encoding='utf-8')
    if 'Methods verified:' not in result.stdout:
        raise RuntimeError('ILVerify could not complete:\n' + result.stdout)
    errors = []
    for line in result.stdout.splitlines():
        if not line.startswith('[IL]: Error'):
            continue
        line = line.replace(str(path.resolve()), '<input>')
        line = re.sub(r'\[offset 0x[0-9A-Fa-f]+\]', '[offset]', line)
        errors.append(line)
    all_results[label] = {'exitCode': result.returncode, 'errors': sorted(errors)}
if all_results['original'] != all_results['patched']:
    raise RuntimeError('Whole-assembly verification diagnostics changed; inspect logs')
report = {'restoredMethodsPassed': 5, 'baselineDiagnosticCount': len(all_results['original']['errors']), 'wholeAssemblyDiagnosticsUnchanged': True}
(a.output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(json.dumps(report, indent=2))
