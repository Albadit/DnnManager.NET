# Docker DNN experiment - windows-2022

- **Run**: 2026-10-05 14:43 UTC
- **Host**: Microsoft Windows NT 10.0.20348.0, Microsoft Windows Server 2022 Datacenter
- **Cpu**: AMD EPYC 9V74 80-Core Processor                , 4 logical
- **Memory**: 16 GB
- **Docker**: client 29.7.2, server 29.7.2 (windows/amd64)
- **Isolation**: process
- **Compose**: 2.40.3
- **DnnVersion**: 10.3.3
- **BindMountChangeNotification**: seen
- **Summary**: 3 of 4 steps passed

| Step | Result | Seconds | Detail |
|---|---|---|---|
| Build the images (SQL Server Express, DNN) | pass | 379 | dnn-docker/dnn:10.3.3 6.73GB; dnn-docker/sqlexpress:2022 6.29GB |
| Start SQL Server and DNN (first start: DNN copied into the site volume, database created) | pass | 12 | answering on http://localhost:8080/ |
| Install DNN unattended (Install.aspx?mode=install with an install template) | FAIL | 14 | docker compose exec -T web powershell -NoProfile -File C:\scripts\after-install.ps1 -HostUser host failed: +          ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~ /     + CategoryInfo          : NotSpecified: (:) [Invoke-Sql], MethodInvocation  /    Exception /     + FullyQualifiedErrorId : SqlException,Invoke-Sql /   |
| File change notifications on a bind-mounted host folder (web.config edited from the host) | pass | 17 | web.config changed on the host -> ASP.NET restarted the app ('one/2026-10-05T14:50:40.1011485+00:00' -> 'two/2026-10-05T14:50:40.1011485+00:00') |

