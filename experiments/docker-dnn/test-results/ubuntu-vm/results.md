# Docker DNN experiment - ubuntu-vm

- **Run**: 2026-10-05 18:01 UTC
- **Host**: Ubuntu 24.04.5 LTS, 4 CPUs, 15.6 GB RAM
- **Kvm**: present
- **Guest**: Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM
- **WindowsReadyAfter**: 7.1 min (first answer from the status site)
- **Stats**: dnn-vm-windows-1 cpu 3.97% mem 8.113GiB / 15.61GiB
- **Disk**: 11G	/mnt/dnn-vm
- **Summary**: 3 of 7 steps passed

| Step | Result | Minutes | Detail |
|---|---|---|---|
| Start the VM container (downloads and installs Windows Server 2022) | pass | 0.2 | started |
| Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install) | pass | 13.9 |     0 s  Installing IIS with ASP.NET 4.8 /    65 s  IIS is up /    65 s  Installing SQL Server 2022 Express /   378 s  SQL Server Express is up /   379 s  Downloading DNN 10.3.3 /   400 s  Running DNN's unattended install /   472 s  DNN 10.3.3 installed /   472 s  READY |
| Load the home page from the Linux host (http://localhost:8080) | FAIL | 0.6 | HTTP 500 (GET / -> 302 /Default.aspx?error=An+unexpected+error+has+occurred&content=0; GET /Default.aspx?error=An+unexpected+error+has+occurred&content=0 -> 500) |
| Sign in as the host | FAIL | 0 | Cannot index into a null array. |
| Install an extension (Google sign-in provider, Persona Bar API) | FAIL | 0 | Not signed in. |
| Restart the VM container (Windows reboots) - the site comes back | FAIL | 20.4 | The site didn't come back within 20 minutes. |
| Resource use (docker stats) and the VM disk | pass | 0 | dnn-vm-windows-1 cpu 3.97% mem 8.113GiB / 15.61GiB / storage 11G	/mnt/dnn-vm |

