import sys
import os
import zipfile

def create_mac_zip(app_dir, zip_path):
    print(f"Creating macOS package: {zip_path}")
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for root, dirs, files in os.walk(app_dir):
            for d in dirs:
                dir_path = os.path.join(root, d)
                rel_dir = os.path.relpath(dir_path, os.path.dirname(app_dir)).replace('\\', '/') + '/'
                zinfo = zipfile.ZipInfo(rel_dir)
                zinfo.create_system = 3  # Unix
                zinfo.external_attr = (0o755 << 16) | 0x10  # directory flag + rwxr-xr-x
                zf.writestr(zinfo, '')

            for f in files:
                file_path = os.path.join(root, f)
                rel_file = os.path.relpath(file_path, os.path.dirname(app_dir)).replace('\\', '/')
                zinfo = zipfile.ZipInfo(rel_file)
                zinfo.create_system = 3  # Unix
                # Mark executables and Mach-O files with +x (0755)
                if '/MacOS/' in rel_file or rel_file.endswith('/Screenshot_One'):
                    zinfo.external_attr = 0o755 << 16
                else:
                    zinfo.external_attr = 0o644 << 16
                with open(file_path, 'rb') as fp:
                    zf.writestr(zinfo, fp.read())

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python package_mac_zip.py <app_dir> <zip_out>")
        sys.exit(1)
    create_mac_zip(sys.argv[1], sys.argv[2])
    print(f"Done! File size: {os.path.getsize(sys.argv[2])} bytes")
