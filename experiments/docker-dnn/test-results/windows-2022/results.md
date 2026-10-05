# Docker DNN experiment - windows-2022

- **Run**: 2026-10-05 18:01 UTC
- **Host**: Microsoft Windows NT 10.0.20348.0, Microsoft Windows Server 2022 Datacenter
- **Cpu**: AMD EPYC 7763 64-Core Processor                , 4 logical
- **Memory**: 16 GB
- **Docker**: client 29.7.2, server 29.7.2 (windows/amd64)
- **Isolation**: process
- **Compose**: 2.40.3
- **DnnVersion**: 10.3.3
- **BindMountChangeNotification**: seen
- **Summary**: 3 of 4 steps passed

| Step | Result | Seconds | Detail |
|---|---|---|---|
| Build the images (SQL Server Express, DNN) | pass | 405 | dnn-docker/dnn:10.3.3 6.73GB; dnn-docker/sqlexpress:2022 6.32GB |
| Start SQL Server and DNN (first start: DNN copied into the site volume, database created) | pass | 13 | answering on http://localhost:8080/ |
| Install DNN unattended (Install.aspx?mode=install with an install template) | FAIL | 31 | DNN's install output doesn't say it completed: DotNetNuke --> Installing DNN Upgrade Error: ERROR: Could not connect to database specified in connectionString for SqlDataProvider DNN's log: 2026-10-05 18:09:05.234+00:00 [cb5f4a912afd][D:2][T:1][ERROR] DotNetNuke.ComponentModel.ProviderInstaller - System.Configuration.ConfigurationErrorsException: Could not load provider DNNConnect.CKEditorProvider.CKHtmlEditorProvider, DNNConnect.CKEditorProvider / 2026-10-05 18:09:06.000+00:00 [cb5f4a912afd][D:2][T:1][ERROR] DotNetNuke.Web.DependencyInjectionInitialize - Unable to configure services for DotNetNuke.Web.Startup, see exception for details / System.IO.FileNotFoundException: Could not load file or assembly 'System.Web.Http, Version=5.3.0.0, Culture=neutral, PublicKeyToken=31bf3856ad3… |
| File change notifications on a bind-mounted host folder (web.config edited from the host) | pass | 18 | web.config changed on the host -> ASP.NET restarted the app ('one/2026-10-05T18:09:34.4513086+00:00' -> 'two/2026-10-05T18:09:34.4513086+00:00') |

