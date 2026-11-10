import sys
import os
import urllib.request
import json

def main():
    if len(sys.argv) < 3:
        print("Usage: python publish_notes.py <token> <version>")
        return

    token = sys.argv[1]
    version = sys.argv[2].strip().lstrip('vV')
    script_dir = os.path.dirname(os.path.abspath(__file__))
    notes_file = os.path.join(script_dir, "ReleaseNotes.md")

    if not os.path.exists(notes_file):
        print(f"Notes file not found at {notes_file}")
        return

    with open(notes_file, "r", encoding="utf-8") as f:
        notes = f.read()

    headers = {
        'User-Agent': 'Screenshot_One_Deploy',
        'Authorization': f'Bearer {token}',
        'Accept': 'application/vnd.github.v3+json',
        'Content-Type': 'application/json'
    }

    url = f'https://api.github.com/repos/Oleksandr-Rosenko/Screenshot_One/releases/tags/{version}'
    try:
        req = urllib.request.Request(url, headers=headers)
        with urllib.request.urlopen(req) as resp:
            rel = json.loads(resp.read().decode('utf-8'))

        rel_id = rel['id']
        patch_url = f'https://api.github.com/repos/Oleksandr-Rosenko/Screenshot_One/releases/{rel_id}'
        patch_data = json.dumps({'body': notes}).encode('utf-8')
        patch_req = urllib.request.Request(patch_url, data=patch_data, method='PATCH', headers=headers)
        with urllib.request.urlopen(patch_req) as resp:
            pass

        print(f"Release notes successfully published to GitHub Release {version}!")
    except Exception as e:
        print(f"Warning: could not update release notes: {e}")

if __name__ == '__main__':
    main()
