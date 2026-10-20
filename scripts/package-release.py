#!/usr/bin/env python3
"""Build turnkey release bundles; optionally reuse the exact Windows EXE tested by CI."""
import argparse, hashlib, os, pathlib, shutil, subprocess, tarfile, zipfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--version', default='0.1.0')
p.add_argument('--windows-exe', type=pathlib.Path)
p.add_argument('--skip-linux', action='store_true')
a = p.parse_args()
if not all(part.isdigit() for part in a.version.split('.')) or len(a.version.split('.')) != 3:
    p.error('version must be a numeric major.minor.patch')
out = ROOT / '.artifacts/release' / ('v' + a.version)
out.mkdir(parents=True, exist_ok=True)
dotnet = os.environ.get('DOTNET', 'dotnet')
notes = '''PCL2 Universal Patcher — third-party tool, not official PCL or PCL CE.

Windows：解压到可写目录，双击“一键修补.cmd”或 pcl-patcher.exe。
不需要安装 .NET。首次运行需要联网，自动下载并校验官方 2.99.0.0 和 2.14.0.0，随后执行补丁。
完成后运行 PCL2-Patched/Plain Craft Launcher 2.exe。

输出：
  PCL2-Patched/Plain Craft Launcher 2.exe                  修补后的启动器
  PCL2-Patched/Plain Craft Launcher 2.exe.pclpatcher.original  原始备份
  PCL2-Patched/Plain Craft Launcher 2.exe.pclpatcher.json    完整性与恢复记录
  PCL2-Patched/logs/                                      详细日志
  PCL2-Patched/.official/                                 校验过的官方文件缓存

重复运行会检查并复用已有结果，不覆盖未知文件。失败请保留完整日志。
手动处理其他 EXE：pcl-patcher.exe --help
恢复：pcl-patcher.exe restore "PCL2-Patched/Plain Craft Launcher 2.exe"

v0.1.0 已于 2026-10-20 获得用户本机验证，用户确认可以正常工作。
七种整合包格式、版本修改及微软登录的逐项测试结果尚未单独记录；详细验证范围见仓库报告。
原作者：龙腾猫跃。官方仓库：https://github.com/Meloong-Git/PCL
工具源码、验证报告：https://github.com/MOPELotus/PCL2-Patcher
'''
assets = []
for rid in ['win-x64'] + ([] if a.skip_linux else ['linux-x64']):
    stage = out / f'PCL2-Patcher-v{a.version}-{rid}'
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir()
    name = 'pcl-patcher.exe' if rid.startswith('win') else 'pcl-patcher'
    if rid == 'win-x64' and a.windows_exe:
        executable = a.windows_exe.resolve()
    else:
        publish = ROOT / '.artifacts/publish' / rid
        subprocess.run([dotnet, 'publish', str(ROOT / 'src/PclPatcher.Cli'), '-c', 'Release', '-r', rid,
                        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None',
                        '-p:DebugSymbols=false', f'-p:Version={a.version}', '-o', str(publish)], check=True)
        executable = publish / name
    shutil.copy2(executable, stage / name)
    (stage / '使用说明.txt').write_text(notes, encoding='utf-8')
    if rid == 'win-x64':
        (stage / '一键修补.cmd').write_bytes(b'@echo off\r\nchcp 65001 >nul\r\n"%~dp0pcl-patcher.exe" auto --output "%~dp0PCL2-Patched"\r\nset "patch_exit=%errorlevel%"\r\npause\r\nexit /b %patch_exit%\r\n')
        archive = out / (stage.name + '.zip')
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
            for file in sorted(stage.iterdir()):
                z.write(file, f'{stage.name}/{file.name}')
        standalone = out / 'pcl-patcher-win-x64.exe'
        shutil.copy2(executable, standalone)
        assets.append(standalone)
    else:
        (stage / name).chmod(0o755)
        (stage / 'run.sh').write_text('#!/bin/sh\ncd -- "$(dirname -- "$0")" || exit 1\nexec ./pcl-patcher auto --output "$PWD/PCL2-Patched"\n', encoding='utf-8')
        (stage / 'run.sh').chmod(0o755)
        archive = out / (stage.name + '.tar.gz')
        with tarfile.open(archive, 'w:gz') as t:
            t.add(stage, arcname=stage.name)
    assets.append(archive)
    print(archive)
checksums = out / 'SHA256SUMS.txt'
checksums.write_text(''.join(hashlib.sha256(asset.read_bytes()).hexdigest() + '  ' + asset.name + '\n' for asset in assets), encoding='utf-8')
print(checksums)
