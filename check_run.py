import urllib.request, json, subprocess, time

t = subprocess.check_output(['gh','auth','token'], text=True).strip()
h = {'Authorization': 'Bearer ' + t, 'Accept': 'application/json'}
rid = 25818778226

for i in range(20):
    url = 'https://api.github.com/repos/punkouter26/PoPunkouterSoftware/actions/runs/' + str(rid)
    r = urllib.request.urlopen(urllib.request.Request(url, headers=h))
    d = json.loads(r.read())
    status = d['status']
    conclusion = d['conclusion']
    print(f'Poll {i}: status={status} conclusion={conclusion}', flush=True)
    if status == 'completed':
        print('Run completed! Conclusion: ' + conclusion)
        break
    time.sleep(30)
else:
    print('Timed out waiting for run to complete')
