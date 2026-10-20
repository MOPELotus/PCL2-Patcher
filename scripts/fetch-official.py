#!/usr/bin/env python3
"""Download original release archives from immutable commits in the OFFICIAL repo."""
import argparse, hashlib, io, pathlib, urllib.request, zipfile
RELEASES = {
    '2.99.0.0': ('6b9a50df911c9d68b69b24c979efba477fb14b67', '6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386'),
    '2.14.0.0': ('5ef81e490ae46b1100d61e33d0183094c7a80ca8', '5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36'),
}
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output', type=pathlib.Path, default=pathlib.Path('.artifacts/official'))
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
for version, (commit, digest) in RELEASES.items():
    destination = a.output / f'PCL-{version}.exe'
    if destination.exists() and hashlib.sha256(destination.read_bytes()).hexdigest() == digest:
        print(f'Verified {destination}')
        continue
    url = f'https://raw.githubusercontent.com/Meloong-Git/PCL/{commit}/' + urllib.parse.quote('最新正式版.zip')
    archive = urllib.request.urlopen(url, timeout=120).read()
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        data = z.read('Plain Craft Launcher 2.exe')
    if hashlib.sha256(data).hexdigest() != digest:
        raise RuntimeError(f'Official fixture digest mismatch: {version}')
    destination.write_bytes(data)
    print(f'Downloaded and verified {destination}')
