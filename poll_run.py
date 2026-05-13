import urllib.request, json, subprocess, time

t = subprocess.check_output(['gh','auth','token'], text=True).strip()
h = {'Authorization': 'Bearer ' + t, 'Accept': 'application/json'}

# Get latest run ID dynamically
r = urllib.request.urlopen(urllib.request.Request(
    'https://api.github.com/repos/punkouter26/PoPunkouterSoftware/actions/runs?per_page=1',
    headers=h))
d = json.loads(r.read())
rid = d['workflow_runs'][0]['id']
print(f'Monitoring run ID: {rid}')

for i in range(30):
    r = urllib.request.urlopen(urllib.request.Request(
        f'https://api.github.com/repos/punkouter26/PoPunkouterSoftware/actions/runs/{rid}',
        headers=h))
    d = json.loads(r.read())
    status = d['status']
    conclusion = d['conclusion']
    print(f'Poll {i}: status={status} conclusion={conclusion}', flush=True)
    if status == 'completed':
        print(f'DONE — conclusion: {conclusion}')
        break
    time.sleep(30)
else:
    print('Timed out')
